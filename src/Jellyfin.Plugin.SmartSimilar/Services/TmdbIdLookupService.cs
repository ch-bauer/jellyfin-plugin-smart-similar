using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Maps TMDb ids to local library items from an in-memory map (Jellyfin's
    /// public API has no efficient provider-id lookup for batches). Same cache
    /// strategy as <see cref="CollectionLookupService"/>: built once, refreshed
    /// in the background on library changes or age-out.
    /// </summary>
    public sealed class TmdbIdLookupService : IDisposable
    {
        private static readonly TimeSpan s_cacheTtl = TimeSpan.FromMinutes(10);

        private readonly ILibraryManager m_libraryManager;
        private readonly ILogger<TmdbIdLookupService> m_logger;
        private volatile Maps? m_cache;
        private DateTime m_builtAt = DateTime.MinValue;
        private int m_rebuilding;

        private sealed record Maps(Dictionary<string, Guid> Movies, Dictionary<string, Guid> Series);

        public TmdbIdLookupService(ILibraryManager libraryManager, ILogger<TmdbIdLookupService> logger)
        {
            m_libraryManager = libraryManager;
            m_logger = logger;
            m_libraryManager.ItemAdded += OnItemChanged;
            m_libraryManager.ItemUpdated += OnItemChanged;
            m_libraryManager.ItemRemoved += OnItemChanged;
        }

        public void Dispose()
        {
            m_libraryManager.ItemAdded -= OnItemChanged;
            m_libraryManager.ItemUpdated -= OnItemChanged;
            m_libraryManager.ItemRemoved -= OnItemChanged;
        }

        public Guid? FindMovie(string tmdbId)
        {
            return EnsureCache().Movies.TryGetValue(tmdbId, out Guid id) ? id : null;
        }

        public Guid? FindSeries(string tmdbId)
        {
            return EnsureCache().Series.TryGetValue(tmdbId, out Guid id) ? id : null;
        }

        /// <summary>Builds the cache ahead of the first request (called at startup).</summary>
        public void Warm()
        {
            try
            {
                EnsureCache();
            }
            catch (Exception ex)
            {
                m_logger.LogWarning(ex, "Failed to warm the TMDb id lookup cache.");
            }
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie || e.Item is Series)
            {
                TriggerBackgroundRebuild();
            }
        }

        private Maps EnsureCache()
        {
            Maps? cache = m_cache;
            if (cache == null)
            {
                return Rebuild();
            }

            if (DateTime.UtcNow - m_builtAt > s_cacheTtl)
            {
                TriggerBackgroundRebuild();
            }

            return cache;
        }

        private void TriggerBackgroundRebuild()
        {
            if (Interlocked.CompareExchange(ref m_rebuilding, 1, 0) != 0)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    Rebuild();
                }
                catch (Exception ex)
                {
                    m_logger.LogWarning(ex, "Failed to rebuild the TMDb id lookup cache.");
                }
                finally
                {
                    Interlocked.Exchange(ref m_rebuilding, 0);
                }
            });
        }

        private Maps Rebuild()
        {
            Dictionary<string, Guid> movies = new Dictionary<string, Guid>();
            Dictionary<string, Guid> series = new Dictionary<string, Guid>();

            IReadOnlyList<BaseItem> items = m_libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            });

            foreach (BaseItem item in items)
            {
                if (!item.TryGetProviderId(MetadataProvider.Tmdb, out string? tmdbId) || string.IsNullOrEmpty(tmdbId))
                {
                    continue;
                }

                if (item is Movie)
                {
                    movies[tmdbId] = item.Id;
                }
                else if (item is Series)
                {
                    series[tmdbId] = item.Id;
                }
            }

            Maps built = new Maps(movies, series);
            m_cache = built;
            m_builtAt = DateTime.UtcNow;
            m_logger.LogDebug("TMDb id lookup cache rebuilt: {Movies} movies, {Series} series.",
                movies.Count, series.Count);
            return built;
        }
    }
}
