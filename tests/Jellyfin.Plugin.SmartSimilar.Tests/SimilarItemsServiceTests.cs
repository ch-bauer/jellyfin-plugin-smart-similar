using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSimilar.Configuration;
using Jellyfin.Plugin.SmartSimilar.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    public class SimilarItemsServiceTests
    {
        [Fact]
        public async Task Local_ExcludesAnchorAndCollectionSiblings()
        {
            Movie anchor = TestData.Movie("Part 1", genres: new[] { "Action" }, tmdbId: "1");
            Movie sibling = TestData.Movie("Part 2", genres: new[] { "Action" }, tmdbId: "2");
            Movie unrelated = TestData.Movie("Other", genres: new[] { "Action" }, tmdbId: "3");

            Fixture fixture = new Fixture(
                movies: new[] { anchor, sibling, unrelated },
                collections: new[] { new[] { anchor, sibling } });

            IReadOnlyList<Guid> result = await fixture.Service.GetSimilarAsync(
                anchor, Guid.Empty,
                new PluginConfiguration { Provider = "Local", MinScore = 0, ExcludeCollectionItems = true },
                CancellationToken.None);

            Assert.Equal(new[] { unrelated.Id }, result);
        }

        [Fact]
        public async Task Local_CollectionFilterDisabled_KeepsSiblings()
        {
            Movie anchor = TestData.Movie("Part 1", genres: new[] { "Action" });
            Movie sibling = TestData.Movie("Part 2", genres: new[] { "Action" });

            Fixture fixture = new Fixture(
                movies: new[] { anchor, sibling },
                collections: new[] { new[] { anchor, sibling } });

            IReadOnlyList<Guid> result = await fixture.Service.GetSimilarAsync(
                anchor, Guid.Empty,
                new PluginConfiguration { Provider = "Local", MinScore = 0, ExcludeCollectionItems = false },
                CancellationToken.None);

            Assert.Contains(sibling.Id, result);
        }

        [Fact]
        public async Task Tmdb_ExcludesSiblingCopiesByTmdbId()
        {
            // The collection links copy A of the sibling, but TMDb maps to copy B
            // (same TMDb id, different library item) - copy B must be excluded too.
            Movie anchor = TestData.Movie("Part 1", genres: new[] { "Action" }, tmdbId: "1");
            Movie siblingCopyA = TestData.Movie("Part 2", genres: new[] { "Action" }, tmdbId: "2");
            Movie siblingCopyB = TestData.Movie("Part 2 (4K)", genres: new[] { "Action" }, tmdbId: "2");
            Movie unrelated = TestData.Movie("Other", genres: new[] { "Action" }, tmdbId: "3");

            Fixture fixture = new Fixture(
                movies: new[] { anchor, siblingCopyA, siblingCopyB, unrelated },
                collections: new[] { new[] { anchor, siblingCopyA } });
            fixture.Handler.Add("/movie/1/recommendations", """{"results":[{"id":2},{"id":3}]}""");

            IReadOnlyList<Guid> result = await fixture.Service.GetSimilarAsync(
                anchor, Guid.Empty,
                new PluginConfiguration { Provider = "Tmdb", TmdbApiKey = "key", ExcludeCollectionItems = true, MinScore = 0 },
                CancellationToken.None);

            Assert.DoesNotContain(siblingCopyA.Id, result);
            Assert.DoesNotContain(siblingCopyB.Id, result);
            Assert.Contains(unrelated.Id, result);
        }

        [Fact]
        public async Task Tmdb_AllRecommendationsExcluded_FallsBackToLocal()
        {
            Movie anchor = TestData.Movie("Part 1", genres: new[] { "Action" }, tmdbId: "1");
            Movie sibling = TestData.Movie("Part 2", genres: new[] { "Action" }, tmdbId: "2");
            Movie localMatch = TestData.Movie("Local Match", genres: new[] { "Action" }, tmdbId: "9");

            Fixture fixture = new Fixture(
                movies: new[] { anchor, sibling, localMatch },
                collections: new[] { new[] { anchor, sibling } });
            // TMDb only recommends the excluded sibling.
            fixture.Handler.Add("/movie/1/recommendations", """{"results":[{"id":2}]}""");

            IReadOnlyList<Guid> result = await fixture.Service.GetSimilarAsync(
                anchor, Guid.Empty,
                new PluginConfiguration { Provider = "Tmdb", TmdbApiKey = "key", ExcludeCollectionItems = true, MinScore = 0 },
                CancellationToken.None);

            Assert.Equal(new[] { localMatch.Id }, result);
        }

        [Fact]
        public async Task Hybrid_TmdbFirst_ThenLocalFill()
        {
            Movie anchor = TestData.Movie("Anchor", genres: new[] { "Action" }, tmdbId: "1");
            Movie tmdbMatch = TestData.Movie("Tmdb Match", genres: new[] { "Drama" }, tmdbId: "2");
            Movie localMatch = TestData.Movie("Local Match", genres: new[] { "Action" }, tmdbId: "3");

            Fixture fixture = new Fixture(movies: new[] { anchor, tmdbMatch, localMatch });
            fixture.Handler.Add("/movie/1/recommendations", """{"results":[{"id":2}]}""");

            IReadOnlyList<Guid> result = await fixture.Service.GetSimilarAsync(
                anchor, Guid.Empty,
                new PluginConfiguration { Provider = "Hybrid", TmdbApiKey = "key", MinScore = 0, ResultLimit = 16 },
                CancellationToken.None);

            // TMDb result first (even though the local match shares more metadata), local fill after.
            Assert.Equal(new[] { tmdbMatch.Id, localMatch.Id }, result);
        }

        [Fact]
        public async Task ResultLimit_IsApplied()
        {
            Movie anchor = TestData.Movie("Anchor", genres: new[] { "Action" });
            Movie[] others = Enumerable.Range(1, 10)
                .Select(i => TestData.Movie("M" + i, genres: new[] { "Action" }))
                .ToArray();

            Fixture fixture = new Fixture(movies: others.Prepend(anchor).ToArray());

            IReadOnlyList<Guid> result = await fixture.Service.GetSimilarAsync(
                anchor, Guid.Empty,
                new PluginConfiguration { Provider = "Local", MinScore = 0, ResultLimit = 3 },
                CancellationToken.None);

            Assert.Equal(3, result.Count);
        }

        /// <summary>
        /// Wires the real services (orchestrator, local scoring, TMDb provider,
        /// collection lookup, people cache) around a single mocked ILibraryManager
        /// and a canned TMDb HTTP handler.
        /// </summary>
        private sealed class Fixture
        {
            public SimilarItemsService Service { get; }

            public FakeHttpHandler Handler { get; } = new();

            public Fixture(Movie[] movies, Movie[][]? collections = null)
            {
                var libraryManager = new Mock<ILibraryManager>();

                List<BaseItem> boxSets = (collections ?? Array.Empty<Movie[]>())
                    .Select((members, index) =>
                    {
                        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = "Collection " + index };
                        boxSet.LinkedChildren = members
                            .Select(m => new LinkedChild { ItemId = m.Id })
                            .ToArray();
                        return (BaseItem)boxSet;
                    })
                    .ToList();

                Dictionary<Guid, BaseItem> byId = movies.ToDictionary(m => m.Id, m => (BaseItem)m);

                libraryManager
                    .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                    .Returns((InternalItemsQuery q) =>
                    {
                        if (q.IncludeItemTypes.Contains(BaseItemKind.BoxSet))
                        {
                            return boxSets;
                        }

                        if (q.ItemIds is { Length: > 0 })
                        {
                            return q.ItemIds
                                .Where(byId.ContainsKey)
                                .Select(id => byId[id])
                                .ToList();
                        }

                        return movies.Cast<BaseItem>().ToList();
                    });
                libraryManager
                    .Setup(l => l.GetItemById(It.IsAny<Guid>()))
                    .Returns((Guid id) => byId.GetValueOrDefault(id));
                libraryManager
                    .Setup(l => l.GetPeople(It.IsAny<BaseItem>()))
                    .Returns(new List<PersonInfo>());

                // BoxSet.GetLinkedChildren resolves through the static service locator.
                BaseItem.LibraryManager = libraryManager.Object;

                var httpClientFactory = new Mock<IHttpClientFactory>();
                httpClientFactory
                    .Setup(f => f.CreateClient(It.IsAny<string>()))
                    .Returns(() => new HttpClient(Handler));

                var peopleCache = new PeopleCacheService(libraryManager.Object, NullLogger<PeopleCacheService>.Instance);
                var localProvider = new LocalScoringProvider(
                    libraryManager.Object, Mock.Of<IUserManager>(), peopleCache,
                    NullLogger<LocalScoringProvider>.Instance);
                var idLookup = new TmdbIdLookupService(libraryManager.Object, NullLogger<TmdbIdLookupService>.Instance);
                var tmdbProvider = new TmdbRecommendationsProvider(
                    httpClientFactory.Object, idLookup, NullLogger<TmdbRecommendationsProvider>.Instance);
                var collectionLookup = new CollectionLookupService(
                    libraryManager.Object, NullLogger<CollectionLookupService>.Instance);

                Service = new SimilarItemsService(
                    localProvider, tmdbProvider, collectionLookup,
                    libraryManager.Object, Mock.Of<IUserManager>(),
                    NullLogger<SimilarItemsService>.Instance);
            }
        }
    }
}
