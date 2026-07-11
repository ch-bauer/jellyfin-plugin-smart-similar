using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Answers "which items share a collection with this item" from an in-memory
    /// map. Enumerating all BoxSets and resolving their linked children takes
    /// hundreds of milliseconds on large libraries, so it is done once, kept
    /// cached, and refreshed in the background when a collection changes or
    /// the cache ages out - requests are never blocked by a rebuild (except
    /// the very first one, which the startup warm-up normally covers).
    /// </summary>
    public sealed class CollectionLookupService : IDisposable
    {
        private static readonly TimeSpan s_cacheTtl = TimeSpan.FromMinutes(10);

        private readonly ILibraryManager m_libraryManager;
        private readonly ILogger<CollectionLookupService> m_logger;
        private volatile List<HashSet<Guid>>? m_cache;
        private DateTime m_builtAt = DateTime.MinValue;
        private int m_rebuilding;

        public CollectionLookupService(ILibraryManager libraryManager, ILogger<CollectionLookupService> logger)
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

        /// <summary>
        /// Returns the ids of all items that are in any collection the given item
        /// belongs to (the item itself excluded).
        /// </summary>
        public HashSet<Guid> GetCollectionSiblings(Guid itemId)
        {
            List<HashSet<Guid>> cache = EnsureCache();
            HashSet<Guid> siblings = new HashSet<Guid>();

            foreach (HashSet<Guid> children in cache)
            {
                if (children.Contains(itemId))
                {
                    siblings.UnionWith(children);
                }
            }

            siblings.Remove(itemId);
            return siblings;
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
                m_logger.LogWarning(ex, "Failed to warm the collection lookup cache.");
            }
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is BoxSet)
            {
                TriggerBackgroundRebuild();
            }
        }

        private List<HashSet<Guid>> EnsureCache()
        {
            List<HashSet<Guid>>? cache = m_cache;
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
                    m_logger.LogWarning(ex, "Failed to rebuild the collection lookup cache.");
                }
                finally
                {
                    Interlocked.Exchange(ref m_rebuilding, 0);
                }
            });
        }

        private List<HashSet<Guid>> Rebuild()
        {
            List<HashSet<Guid>> built = m_libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                    CollapseBoxSetItems = false,
                    Recursive = true
                })
                .OfType<BoxSet>()
                .Select(boxSet => boxSet.GetLinkedChildren().Select(child => child.Id).ToHashSet())
                .ToList();

            m_cache = built;
            m_builtAt = DateTime.UtcNow;
            m_logger.LogDebug("Collection lookup cache rebuilt with {Count} collections.", built.Count);
            return built;
        }
    }
}
