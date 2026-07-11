using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Caches the scoring-relevant people (directors, writers, top-billed
    /// actors) per item. Resolving people is a DB query per item and is the
    /// dominant cost of a scoring pass, so the whole library is warmed once at
    /// startup and entries are dropped (and lazily reloaded) when an item
    /// changes.
    /// </summary>
    public sealed class PeopleCacheService : IDisposable
    {
        private const int WarmParallelism = 8;
        private const int MaxDirectors = 4;
        private const int MaxWriters = 6;
        private const int MaxActors = 5;

        public sealed record PeopleEntry(
            IReadOnlyList<string> Directors,
            IReadOnlyList<string> Writers,
            IReadOnlyList<string> TopActors);

        private static readonly PeopleEntry s_empty = new PeopleEntry(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        private readonly ILibraryManager m_libraryManager;
        private readonly ILogger<PeopleCacheService> m_logger;
        private readonly ConcurrentDictionary<Guid, PeopleEntry> m_cache = new();

        public PeopleCacheService(ILibraryManager libraryManager, ILogger<PeopleCacheService> logger)
        {
            m_libraryManager = libraryManager;
            m_logger = logger;
            m_libraryManager.ItemUpdated += OnItemChanged;
            m_libraryManager.ItemRemoved += OnItemChanged;
        }

        public void Dispose()
        {
            m_libraryManager.ItemUpdated -= OnItemChanged;
            m_libraryManager.ItemRemoved -= OnItemChanged;
        }

        public PeopleEntry GetOrLoad(BaseItem item)
        {
            return m_cache.GetOrAdd(item.Id, _ => Load(item));
        }

        /// <summary>Loads the people of every movie and series into the cache (called at startup).</summary>
        public void Warm(IProgress<double>? progress, CancellationToken cancellationToken)
        {
            IReadOnlyList<BaseItem> items = m_libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            });

            int done = 0;
            Parallel.ForEach(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = WarmParallelism, CancellationToken = cancellationToken },
                item =>
                {
                    GetOrLoad(item);
                    int current = Interlocked.Increment(ref done);
                    if (progress != null && current % 50 == 0)
                    {
                        progress.Report(100.0 * current / items.Count);
                    }
                });

            m_logger.LogInformation("People cache warmed for {Count} items.", items.Count);
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is Movie || e.Item is Series)
            {
                m_cache.TryRemove(e.Item.Id, out _);
            }
        }

        private PeopleEntry Load(BaseItem item)
        {
            try
            {
                List<string> directors = new List<string>(2);
                List<string> writers = new List<string>(2);
                List<string> actors = new List<string>(MaxActors);

                foreach (PersonInfo person in m_libraryManager.GetPeople(item))
                {
                    if (string.IsNullOrEmpty(person.Name))
                    {
                        continue;
                    }

                    if (person.Type == PersonKind.Director && directors.Count < MaxDirectors)
                    {
                        directors.Add(person.Name);
                    }
                    else if (person.Type == PersonKind.Writer && writers.Count < MaxWriters)
                    {
                        writers.Add(person.Name);
                    }
                    else if (person.Type == PersonKind.Actor && actors.Count < MaxActors)
                    {
                        actors.Add(person.Name);
                    }
                }

                if (directors.Count == 0 && writers.Count == 0 && actors.Count == 0)
                {
                    return s_empty;
                }

                return new PeopleEntry(directors, writers, actors);
            }
            catch (Exception ex)
            {
                m_logger.LogDebug(ex, "Could not resolve people for {Item}.", item.Name);
                return s_empty;
            }
        }
    }
}
