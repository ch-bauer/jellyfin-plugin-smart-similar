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
    /// Ranks library items by metadata similarity to an anchor item. All signals
    /// except people come straight off the in-memory candidate list, so a full
    /// scan per request stays cheap; people (a DB query per item) are only
    /// resolved for the best base-scored candidates and used to re-rank them.
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

        /// <summary>Cap on how many top base-scored candidates get the (per-item DB query) people pass.</summary>
        private const int PeopleRerankCap = 150;

        /// <summary>Concurrent people queries during the re-rank pass.</summary>
        private const int PeopleQueryParallelism = 8;

        // A full scoring pass costs a candidate scan plus up to PeopleRerankCount
        // people queries, so the ranking is kept for a while. Metadata changes
        // show up after at most the TTL - the same trade-off as the other caches.
        private static readonly TimeSpan s_cacheTtl = TimeSpan.FromMinutes(10);
        private const int MaxCacheEntries = 256;

        private readonly ConcurrentDictionary<string, CacheEntry> m_cache = new();

        private sealed record CacheEntry(DateTime BuiltAt, IReadOnlyList<Guid> Ids);

        private readonly ILibraryManager m_libraryManager;
        private readonly IUserManager m_userManager;
        private readonly ILogger<LocalScoringProvider> m_logger;

        public LocalScoringProvider(
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogger<LocalScoringProvider> logger)
        {
            m_libraryManager = libraryManager;
            m_userManager = userManager;
            m_logger = logger;
        }

        /// <summary>
        /// Returns all candidate item ids scoring at least <see cref="PluginConfiguration.MinScore"/>,
        /// best match first. The caller applies exclusions and the result limit.
        /// </summary>
        public IReadOnlyList<Guid> GetScored(BaseItem anchor, Guid userId, PluginConfiguration config)
        {
            // The ranking depends on the anchor, the user (library access
            // filtering), the score threshold and the re-rank width (derived
            // from the result limit) - key on all of them.
            string cacheKey = $"{anchor.Id:N}:{userId:N}:{config.MinScore}:{config.ResultLimit}";
            if (m_cache.TryGetValue(cacheKey, out CacheEntry? cached)
                && DateTime.UtcNow - cached.BuiltAt < s_cacheTtl)
            {
                return cached.Ids;
            }

            IReadOnlyList<Guid> scoredIds = ComputeScored(anchor, userId, config);
            StoreInCache(cacheKey, scoredIds);
            return scoredIds;
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
            var user = userId == Guid.Empty ? null : m_userManager.GetUserById(userId);

            InternalItemsQuery query = user == null ? new InternalItemsQuery() : new InternalItemsQuery(user);
            query.IncludeItemTypes = new[] { anchor is Series ? BaseItemKind.Series : BaseItemKind.Movie };
            query.Recursive = true;
            query.IsVirtualItem = false;

            IReadOnlyList<BaseItem> candidates = m_libraryManager.GetItemList(query);

            AnchorFeatures anchorFeatures = BuildAnchorFeatures(anchor);

            List<Scored> scored = new List<Scored>(candidates.Count);
            foreach (BaseItem candidate in candidates)
            {
                if (candidate.Id == anchor.Id)
                {
                    continue;
                }

                scored.Add(new Scored(candidate, BaseScore(anchorFeatures, candidate)));
            }

            scored.Sort(CompareScored);

            // People pass: only for the best base-scored candidates - people add
            // at most 20 points, so anything far down the base ranking cannot
            // reach the visible row anyway. Sized to the row, since each people
            // lookup is a DB query; queried in parallel to keep cold requests fast.
            int rerankCount = Math.Min(
                Math.Min(PeopleRerankCap, Math.Max(config.ResultLimit * 4, 48)),
                scored.Count);
            if (anchorFeatures.HasPeople && rerankCount > 0)
            {
                double[] peopleScores = new double[rerankCount];
                Parallel.For(
                    0,
                    rerankCount,
                    new ParallelOptions { MaxDegreeOfParallelism = PeopleQueryParallelism },
                    i => peopleScores[i] = PeopleScore(anchorFeatures, scored[i].Item));

                for (int i = 0; i < rerankCount; i++)
                {
                    scored[i] = scored[i] with { Score = scored[i].Score + peopleScores[i] };
                }

                scored.Sort(CompareScored);
            }

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
            HashSet<string> directors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> writers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (PersonInfo person in m_libraryManager.GetPeople(anchor))
                {
                    if (string.IsNullOrEmpty(person.Name))
                    {
                        continue;
                    }

                    if (person.Type == PersonKind.Director && directors.Count < 2)
                    {
                        directors.Add(person.Name);
                    }
                    else if (person.Type == PersonKind.Writer && writers.Count < 2)
                    {
                        writers.Add(person.Name);
                    }
                    else if (person.Type == PersonKind.Actor && actors.Count < 5)
                    {
                        actors.Add(person.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger.LogDebug(ex, "Could not resolve people for {Anchor}; scoring without people.", anchor.Name);
            }

            return new AnchorFeatures
            {
                Genres = new HashSet<string>(anchor.Genres ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                Tags = new HashSet<string>(anchor.Tags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                Studios = new HashSet<string>(anchor.Studios ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase),
                Directors = directors,
                Writers = writers,
                Actors = actors,
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
            double score = 0;

            try
            {
                int candidateActors = 0;
                foreach (PersonInfo person in m_libraryManager.GetPeople(candidate))
                {
                    if (string.IsNullOrEmpty(person.Name))
                    {
                        continue;
                    }

                    if (person.Type == PersonKind.Director && anchor.Directors.Contains(person.Name))
                    {
                        score += DirectorPoints;
                    }
                    else if (person.Type == PersonKind.Writer && anchor.Writers.Contains(person.Name))
                    {
                        score += WriterPoints;
                    }
                    else if (person.Type == PersonKind.Actor)
                    {
                        // Only the candidate's top billing can match the anchor's top actors.
                        candidateActors++;
                        if (candidateActors <= 5 && anchor.Actors.Contains(person.Name))
                        {
                            score += ActorPoints;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return 0;
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
