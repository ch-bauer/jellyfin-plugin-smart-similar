using Jellyfin.Plugin.SmartSimilar.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
            if (config.ExcludeCollectionItems)
            {
                excluded.UnionWith(m_collectionLookup.GetCollectionSiblings(anchor.Id));
            }

            List<Guid> ordered = new List<Guid>();
            HashSet<Guid> seen = new HashSet<Guid>();

            if (config.Provider is "Tmdb" or "Hybrid")
            {
                foreach (Guid id in await m_tmdbProvider.GetRecommendedAsync(anchor, config, cancellationToken).ConfigureAwait(false))
                {
                    if (seen.Add(id))
                    {
                        ordered.Add(id);
                    }
                }
            }

            // Local scoring runs for: the Local provider, Hybrid fill, and as a
            // fallback when TMDb produced nothing (no key, no TMDb id, API error).
            if (config.Provider != "Tmdb" || ordered.Count == 0)
            {
                if (config.Provider == "Tmdb")
                {
                    m_logger.LogDebug("TMDb returned no results for {Name}; using local scoring instead.", anchor.Name);
                }

                foreach (Guid id in m_localProvider.GetScored(anchor, userId, config))
                {
                    if (seen.Add(id))
                    {
                        ordered.Add(id);
                    }
                }
            }

            List<Guid> filtered = ordered.Where(id => !excluded.Contains(id)).ToList();

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

            return filtered.Take(Math.Clamp(config.ResultLimit, 1, 64)).ToList();
        }
    }
}
