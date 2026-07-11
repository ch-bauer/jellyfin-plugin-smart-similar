using Jellyfin.Plugin.SmartSimilar.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Orchestrates the similarity providers and applies the exclusion rules
    /// (current item, collection siblings, watched items) and the result limit.
    /// </summary>
    public sealed class SimilarItemsService
    {
        private readonly LocalScoringProvider m_localProvider;
        private readonly TmdbRecommendationsProvider m_tmdbProvider;
        private readonly CollectionLookupService m_collectionLookup;
        private readonly ILibraryManager m_libraryManager;
        private readonly IUserManager m_userManager;
        private readonly ILogger<SimilarItemsService> m_logger;

        public SimilarItemsService(
            LocalScoringProvider localProvider,
            TmdbRecommendationsProvider tmdbProvider,
            CollectionLookupService collectionLookup,
            ILibraryManager libraryManager,
            IUserManager userManager,
            ILogger<SimilarItemsService> logger)
        {
            m_localProvider = localProvider;
            m_tmdbProvider = tmdbProvider;
            m_collectionLookup = collectionLookup;
            m_libraryManager = libraryManager;
            m_userManager = userManager;
            m_logger = logger;
        }

        public async Task<IReadOnlyList<Guid>> GetSimilarAsync(
            BaseItem anchor, Guid userId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            HashSet<Guid> excluded = new HashSet<Guid> { anchor.Id };

            // Exclusion also works on TMDb ids: when the same movie exists as
            // several library items (copies in different libraries, editions),
            // the collection links one item while the TMDb mapping may resolve
            // to another - those siblings would slip past a pure id filter.
            HashSet<string> excludedTmdbIds = new HashSet<string>(StringComparer.Ordinal);
            AddTmdbId(excludedTmdbIds, anchor);

            if (config.ExcludeCollectionItems)
            {
                HashSet<Guid> siblings = m_collectionLookup.GetCollectionSiblings(anchor.Id);
                excluded.UnionWith(siblings);
                foreach (Guid siblingId in siblings)
                {
                    AddTmdbId(excludedTmdbIds, m_libraryManager.GetItemById(siblingId));
                }
            }

            List<Guid> ordered = new List<Guid>();
            HashSet<Guid> seen = new HashSet<Guid>();

            if (config.Provider is "Tmdb" or "Hybrid")
            {
                foreach (Guid id in await m_tmdbProvider.GetRecommendedAsync(anchor, config, cancellationToken).ConfigureAwait(false))
                {
                    if (excluded.Contains(id) || HasExcludedTmdbId(id, excludedTmdbIds))
                    {
                        continue;
                    }

                    if (seen.Add(id))
                    {
                        ordered.Add(id);
                    }
                }
            }

            // Local scoring runs for: the Local provider, Hybrid fill, and as a
            // fallback when TMDb contributed nothing usable - no key, no TMDb id,
            // API error, or (in small libraries) every recommendation excluded.
            if (config.Provider != "Tmdb" || ordered.Count == 0)
            {
                if (config.Provider == "Tmdb")
                {
                    m_logger.LogDebug("TMDb yielded no usable results for {Name}; using local scoring instead.", anchor.Name);
                }

                foreach (Guid id in m_localProvider.GetScored(anchor, userId, config))
                {
                    if (excluded.Contains(id))
                    {
                        continue;
                    }

                    if (seen.Add(id))
                    {
                        ordered.Add(id);
                    }
                }
            }

            List<Guid> filtered = ordered;

            if (config.ExcludeWatched && filtered.Count > 0)
            {
                var user = userId == Guid.Empty ? null : m_userManager.GetUserById(userId);
                if (user != null)
                {
                    HashSet<Guid> unplayed = m_libraryManager.GetItemList(new InternalItemsQuery(user)
                        {
                            ItemIds = filtered.ToArray(),
                            IsPlayed = false
                        })
                        .Select(item => item.Id)
                        .ToHashSet();
                    filtered = filtered.Where(unplayed.Contains).ToList();
                }
            }

            int limit = Math.Clamp(config.ResultLimit, 1, 64);
            List<Guid> result = new List<Guid>(limit);

            foreach (Guid id in filtered)
            {
                if (result.Count == limit)
                {
                    break;
                }

                // Covers duplicate copies from the local list (the TMDb list is
                // already tmdb-id-filtered); bounded by the result limit.
                if (HasExcludedTmdbId(id, excludedTmdbIds))
                {
                    continue;
                }

                result.Add(id);
            }

            return result;
        }

        private bool HasExcludedTmdbId(Guid id, HashSet<string> excludedTmdbIds)
        {
            if (excludedTmdbIds.Count == 0)
            {
                return false;
            }

            BaseItem? item = m_libraryManager.GetItemById(id);
            return item != null
                   && item.TryGetProviderId(MetadataProvider.Tmdb, out string? tmdbId)
                   && !string.IsNullOrEmpty(tmdbId)
                   && excludedTmdbIds.Contains(tmdbId);
        }

        private static void AddTmdbId(HashSet<string> tmdbIds, BaseItem? item)
        {
            if (item != null
                && item.TryGetProviderId(MetadataProvider.Tmdb, out string? tmdbId)
                && !string.IsNullOrEmpty(tmdbId))
            {
                tmdbIds.Add(tmdbId);
            }
        }
    }
}
