using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSimilar.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Ranks library items by metadata similarity to an anchor item. All
    /// signals come from in-memory data: the candidate list is cached per media
    /// type and user (served stale while a background refresh runs, so requests
    /// never block on a library scan), and people come from
    /// <see cref="PeopleCacheService"/>, which is warmed at startup.
    /// </summary>
    public sealed class LocalScoringProvider
    {
        // Score budget (max 100):
        private const double GenreWeight = 35;
        private const double TagWeight = 20;
        private const double PeopleWeight = 20;
        private const double YearWeight = 10;
        private const double StudioWeight = 5;
        private const double CommunityRatingWeight = 5;
        private const double OfficialRatingWeight = 5;

        private const double DirectorPoints = 10;
        private const double WriterPoints = 4;
        private const double ActorPoints = 3;

        private static readonly TimeSpan s_cacheTtl = TimeSpan.FromMinutes(10);
        private const int MaxCacheEntries = 256;

        private readonly ConcurrentDictionary<string, CacheEntry> m_cache = new();

        private sealed record CacheEntry(DateTime BuiltAt, IReadOnlyList<Guid> Ids);

        // Materializing every Movie/Series item is the single biggest cost of a
        // scoring pass on large libraries, and the list is identical for every
        // anchor of the same kind - cache it per kind and user, serve it stale
        // while a background refresh runs.
        private readonly ConcurrentDictionary<string, CandidateEntry> m_candidateCache = new();
        private readonly ConcurrentDictionary<string, byte> m_candidateRefreshing = new();

        private sealed record CandidateEntry(DateTime BuiltAt, IReadOnlyList<BaseItem> Items);

        private readonly ILibraryManager m_libraryManager;
        private readonly IUserManager m_userManager;
        private readonly PeopleCacheService m_peopleCache;
        private readonly ILogger<LocalScoringProvider> m_logger;

        public LocalScoringProvider(
            ILibraryManager libraryManager,
            IUserManager userManager,
            PeopleCacheService peopleCache,
            ILogger<LocalScoringProvider> logger)
        {
            m_libraryManager = libraryManager;
            m_userManager = userManager;
            m_peopleCache = peopleCache;
            m_logger = logger;
        }

        /// <summary>
        /// Returns all candidate item ids scoring at least <see cref="PluginConfiguration.MinScore"/>,
        /// best match first. The caller applies exclusions and the result limit.
        /// </summary>
        public IReadOnlyList<Guid> GetScored(BaseItem anchor, Guid userId, PluginConfiguration config)
        {
            // The ranking depends on the anchor, the user (library access
            // filtering) and the score threshold - key on all three.
            string cacheKey = $"{anchor.Id:N}:{userId:N}:{config.MinScore}";
            if (m_cache.TryGetValue(cacheKey, out CacheEntry? cached)
                && DateTime.UtcNow - cached.BuiltAt < s_cacheTtl)
            {
                return cached.Ids;
            }

            IReadOnlyList<Guid> scoredIds = ComputeScored(anchor, userId, config);
            StoreInCache(cacheKey, scoredIds);
            return scoredIds;
        }

        /// <summary>Fills the candidate caches ahead of the first request (called at startup).</summary>
        public void WarmCandidates(Guid userId)
        {
            try
            {
                GetCandidates(isSeries: false, userId);
                GetCandidates(isSeries: true, userId);
            }
            catch (Exception ex)
            {
                m_logger.LogWarning(ex, "Failed to warm the candidate cache for user {UserId}.", userId);
            }
        }

        private void StoreInCache(string cacheKey, IReadOnlyList<Guid> ids)
        {
            if (m_cache.Count >= MaxCacheEntries)
            {
                // Drop the oldest entry; precise LRU is not worth the bookkeeping here.
                string? oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (KeyValuePair<string, CacheEntry> entry in m_cache)
                {
                    if (entry.Value.BuiltAt < oldest)
                    {
                        oldest = entry.Value.BuiltAt;
                        oldestKey = entry.Key;
                    }
                }

                if (oldestKey != null)
                {
                    m_cache.TryRemove(oldestKey, out _);
                }
            }

            m_cache[cacheKey] = new CacheEntry(DateTime.UtcNow, ids);
        }

        private IReadOnlyList<Guid> ComputeScored(BaseItem anchor, Guid userId, PluginConfiguration config)
        {
            IReadOnlyList<BaseItem> candidates = GetCandidates(anchor is Series, userId);

            AnchorFeatures anchorFeatures = BuildAnchorFeatures(anchor);

            List<Scored> scored = new List<Scored>(candidates.Count);
            foreach (BaseItem candidate in candidates)
            {
                if (candidate.Id == anchor.Id)
                {
                    continue;
                }

                // People lookups are dictionary hits after the startup warm-up,
                // so every candidate gets the full score in one pass.
                double score = BaseScore(anchorFeatures, candidate);
                if (anchorFeatures.HasPeople)
                {
                    score += PeopleScore(anchorFeatures, candidate);
                }

                scored.Add(new Scored(candidate, score));
            }

            scored.Sort(CompareScored);

            List<Guid> result = new List<Guid>();
            foreach (Scored entry in scored)
            {
                if (entry.Score < config.MinScore)
                {
                    break;
                }

                result.Add(entry.Item.Id);
            }

            m_logger.LogDebug("Local scoring for {Anchor}: {Candidates} candidates, {Matches} above min score.",
                anchor.Name, candidates.Count, result.Count);
            return result;
        }

        private IReadOnlyList<BaseItem> GetCandidates(bool isSeries, Guid userId)
        {
            string cacheKey = (isSeries ? "series" : "movie") + ":" + userId.ToString("N");
            if (m_candidateCache.TryGetValue(cacheKey, out CandidateEntry? cached))
            {
                // Serve stale and refresh in the background - a request should
                // never block on a full library scan.
                if (DateTime.UtcNow - cached.BuiltAt > s_cacheTtl
                    && m_candidateRefreshing.TryAdd(cacheKey, 0))
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            LoadCandidates(cacheKey, isSeries, userId);
                        }
                        catch (Exception ex)
                        {
                            m_logger.LogWarning(ex, "Failed to refresh the candidate cache.");
                        }
                        finally
                        {
                            m_candidateRefreshing.TryRemove(cacheKey, out _);
                        }
                    });
                }

                return cached.Items;
            }

            return LoadCandidates(cacheKey, isSeries, userId);
        }

        private IReadOnlyList<BaseItem> LoadCandidates(string cacheKey, bool isSeries, Guid userId)
        {
            var user = userId == Guid.Empty ? null : m_userManager.GetUserById(userId);

            InternalItemsQuery query = user == null ? new InternalItemsQuery() : new InternalItemsQuery(user);
            query.IncludeItemTypes = new[] { isSeries ? BaseItemKind.Series : BaseItemKind.Movie };
            query.Recursive = true;
            query.IsVirtualItem = false;

            IReadOnlyList<BaseItem> candidates = m_libraryManager.GetItemList(query);
            m_candidateCache[cacheKey] = new CandidateEntry(DateTime.UtcNow, candidates);
            return candidates;
        }

        private sealed record Scored(BaseItem Item, double Score);

        private static int CompareScored(Scored a, Scored b)
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            int byRating = (b.Item.CommunityRating ?? 0).CompareTo(a.Item.CommunityRating ?? 0);
            if (byRating != 0)
            {
                return byRating;
            }

            return string.Compare(a.Item.SortName, b.Item.SortName, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class AnchorFeatures
        {
            public required HashSet<string> Genres { get; init; }
            public required HashSet<string> Tags { get; init; }
            public required HashSet<string> Studios { get; init; }
            public required HashSet<string> Directors { get; init; }
            public required HashSet<string> Writers { get; init; }
            public required HashSet<string> Actors { get; init; }
            public int? Year { get; init; }
            public float? CommunityRating { get; init; }
            public string? OfficialRating { get; init; }

            public bool HasPeople => Directors.Count > 0 || Writers.Count > 0 || Actors.Count > 0;
        }

        private AnchorFeatures BuildAnchorFeatures(BaseItem anchor)
        {
            PeopleCacheService.PeopleEntry people = m_peopleCache.GetOrLoad(anchor);

            return new AnchorFeatures
            {
                Genres = new HashSet<string>(anchor.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                Tags = new HashSet<string>(anchor.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                Studios = new HashSet<string>(anchor.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                Directors = new HashSet<string>(people.Directors.Take(2), StringComparer.OrdinalIgnoreCase),
                Writers = new HashSet<string>(people.Writers.Take(2), StringComparer.OrdinalIgnoreCase),
                Actors = new HashSet<string>(people.TopActors, StringComparer.OrdinalIgnoreCase),
                Year = anchor.ProductionYear,
                CommunityRating = anchor.CommunityRating,
                OfficialRating = anchor.OfficialRating
            };
        }

        private static double BaseScore(AnchorFeatures anchor, BaseItem candidate)
        {
            double score = 0;

            if (anchor.Genres.Count > 0)
            {
                int sharedGenres = CountShared(anchor.Genres, candidate.Genres);
                score += GenreWeight * sharedGenres / anchor.Genres.Count;
            }

            if (anchor.Tags.Count > 0)
            {
                int sharedTags = CountShared(anchor.Tags, candidate.Tags);
                score += Math.Min(TagWeight, TagWeight * sharedTags / Math.Min(anchor.Tags.Count, 5));
            }

            if (anchor.Year.HasValue && candidate.ProductionYear.HasValue)
            {
                int yearDiff = Math.Abs(anchor.Year.Value - candidate.ProductionYear.Value);
                score += YearWeight * Math.Max(0, 1 - yearDiff / 20.0);
            }

            if (anchor.Studios.Count > 0 && CountShared(anchor.Studios, candidate.Studios) > 0)
            {
                score += StudioWeight;
            }

            if (anchor.CommunityRating.HasValue && candidate.CommunityRating.HasValue)
            {
                double ratingDiff = Math.Abs(anchor.CommunityRating.Value - candidate.CommunityRating.Value);
                score += CommunityRatingWeight * Math.Max(0, 1 - ratingDiff / 3.0);
            }

            if (!string.IsNullOrEmpty(anchor.OfficialRating)
                && string.Equals(anchor.OfficialRating, candidate.OfficialRating, StringComparison.OrdinalIgnoreCase))
            {
                score += OfficialRatingWeight;
            }

            return score;
        }

        private double PeopleScore(AnchorFeatures anchor, BaseItem candidate)
        {
            PeopleCacheService.PeopleEntry people = m_peopleCache.GetOrLoad(candidate);
            double score = 0;

            foreach (string director in people.Directors)
            {
                if (anchor.Directors.Contains(director))
                {
                    score += DirectorPoints;
                }
            }

            foreach (string writer in people.Writers)
            {
                if (anchor.Writers.Contains(writer))
                {
                    score += WriterPoints;
                }
            }

            foreach (string actor in people.TopActors)
            {
                if (anchor.Actors.Contains(actor))
                {
                    score += ActorPoints;
                }
            }

            return Math.Min(PeopleWeight, score);
        }

        private static int CountShared(HashSet<string> anchorValues, string[]? candidateValues)
        {
            if (candidateValues == null || candidateValues.Length == 0)
            {
                return 0;
            }

            int shared = 0;
            foreach (string value in candidateValues)
            {
                if (anchorValues.Contains(value))
                {
                    shared++;
                }
            }

            return shared;
        }
    }
}
