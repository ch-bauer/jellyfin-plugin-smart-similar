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
            int limit = Math.Clamp(config.ResultLimit, 1, 64);

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

                // One batch query instead of a lookup per sibling - collections can be large.
                if (siblings.Count > 0)
                {
                    foreach (BaseItem sibling in m_libraryManager.GetItemList(new InternalItemsQuery
                             {
                                 ItemIds = siblings.ToArray()
                             }))
                    {
                        AddTmdbId(excludedTmdbIds, sibling);
                    }
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

                if (config.ExcludeWatched)
                {
                    ordered = FilterUnplayed(ordered, userId);
                }
            }

            // Local scoring is the expensive step (full candidate scan), so it
            // only runs when its results can actually appear in the row: the
            // Local provider, Hybrid with unfilled slots, and Tmdb as fallback
            // when TMDb contributed nothing usable (no key, no TMDb id, API
            // error, or every recommendation excluded).
            bool needLocal = config.Provider switch
            {
                "Tmdb" => ordered.Count == 0,
                "Hybrid" => ordered.Count < limit,
                _ => true
            };

            if (needLocal)
            {
                if (config.Provider == "Tmdb")
                {
                    m_logger.LogDebug("TMDb yielded no usable results for {Name}; using local scoring instead.", anchor.Name);
                }

                List<Guid> local = new List<Guid>();
                foreach (Guid id in m_localProvider.GetScored(anchor, userId, config))
                {
                    if (excluded.Contains(id))
                    {
                        continue;
                    }

                    if (seen.Add(id))
                    {
                        local.Add(id);
                    }

                    // The filters below can only remove items, so a bounded
                    // prefix of the ranking is enough to fill the row - and it
                    // keeps the watched-filter query small.
                    if (local.Count >= Math.Max(limit * 4, 64))
                    {
                        break;
                    }
                }

                if (config.ExcludeWatched)
                {
                    local = FilterUnplayed(local, userId);
                }

                ordered.AddRange(local);
            }

            List<Guid> result = new List<Guid>(limit);

            foreach (Guid id in ordered)
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

        private List<Guid> FilterUnplayed(List<Guid> ids, Guid userId)
        {
            if (ids.Count == 0)
            {
                return ids;
            }

            var user = userId == Guid.Empty ? null : m_userManager.GetUserById(userId);
            if (user == null)
            {
                return ids;
            }

            HashSet<Guid> unplayed = m_libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    ItemIds = ids.ToArray(),
                    IsPlayed = false
                })
                .Select(item => item.Id)
                .ToHashSet();

            return ids.Where(unplayed.Contains).ToList();
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
