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
    public class TmdbRecommendationsProviderTests
    {
        [Fact]
        public void CreateRequest_V3Key_UsesQueryParameter()
        {
            using HttpRequestMessage request = TmdbRecommendationsProvider.CreateRequest(
                "https://api.themoviedb.org/3/movie/1/recommendations?page=1", "abc123");

            Assert.Null(request.Headers.Authorization);
            Assert.Contains("api_key=abc123", request.RequestUri!.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void CreateRequest_V4Token_UsesBearerHeader()
        {
            using HttpRequestMessage request = TmdbRecommendationsProvider.CreateRequest(
                "https://api.themoviedb.org/3/movie/1/recommendations?page=1", "eyJhbGciOi");

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.DoesNotContain("api_key", request.RequestUri!.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetRecommendedAsync_MapsResultsToLibraryInOrder()
        {
            Movie anchor = TestData.Movie("Anchor", tmdbId: "100");
            Movie inLibrary1 = TestData.Movie("First", tmdbId: "201");
            Movie inLibrary2 = TestData.Movie("Second", tmdbId: "202");

            (TmdbRecommendationsProvider provider, FakeHttpHandler handler) =
                CreateProvider(new[] { anchor, inLibrary1, inLibrary2 });

            // 999 is not in the library, 100 is the anchor itself; order must be kept.
            handler.Add("/movie/100/recommendations",
                """{"results":[{"id":202},{"id":999},{"id":100},{"id":201}]}""");

            IReadOnlyList<Guid> ids = await provider.GetRecommendedAsync(
                anchor, new PluginConfiguration { TmdbApiKey = "key" }, CancellationToken.None);

            Assert.Equal(new[] { inLibrary2.Id, inLibrary1.Id }, ids);
        }

        [Fact]
        public async Task GetRecommendedAsync_WithoutKey_ReturnsEmptyWithoutRequest()
        {
            Movie anchor = TestData.Movie("Anchor", tmdbId: "100");
            (TmdbRecommendationsProvider provider, FakeHttpHandler handler) = CreateProvider(new[] { anchor });

            IReadOnlyList<Guid> ids = await provider.GetRecommendedAsync(
                anchor, new PluginConfiguration { TmdbApiKey = "" }, CancellationToken.None);

            Assert.Empty(ids);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task GetRecommendedAsync_WithoutTmdbId_ReturnsEmpty()
        {
            Movie anchor = TestData.Movie("Anchor");
            (TmdbRecommendationsProvider provider, _) = CreateProvider(new[] { anchor });

            IReadOnlyList<Guid> ids = await provider.GetRecommendedAsync(
                anchor, new PluginConfiguration { TmdbApiKey = "key" }, CancellationToken.None);

            Assert.Empty(ids);
        }

        [Fact]
        public async Task GetRecommendedAsync_ApiError_ReturnsEmpty()
        {
            Movie anchor = TestData.Movie("Anchor", tmdbId: "100");
            (TmdbRecommendationsProvider provider, FakeHttpHandler handler) = CreateProvider(new[] { anchor });
            handler.Add("/movie/100/recommendations", """{"status_message":"nope"}""", System.Net.HttpStatusCode.Unauthorized);
            handler.Add("/movie/100/similar", """{"status_message":"nope"}""", System.Net.HttpStatusCode.Unauthorized);

            IReadOnlyList<Guid> ids = await provider.GetRecommendedAsync(
                anchor, new PluginConfiguration { TmdbApiKey = "key" }, CancellationToken.None);

            Assert.Empty(ids);
        }

        [Fact]
        public async Task GetRecommendedAsync_EmptyRecommendations_FallsBackToSimilarEndpoint()
        {
            Movie anchor = TestData.Movie("Anchor", tmdbId: "100");
            Movie other = TestData.Movie("Other", tmdbId: "300");
            (TmdbRecommendationsProvider provider, FakeHttpHandler handler) = CreateProvider(new[] { anchor, other });

            handler.Add("/movie/100/recommendations", """{"results":[]}""");
            handler.Add("/movie/100/similar", """{"results":[{"id":300}]}""");

            IReadOnlyList<Guid> ids = await provider.GetRecommendedAsync(
                anchor, new PluginConfiguration { TmdbApiKey = "key" }, CancellationToken.None);

            Assert.Equal(new[] { other.Id }, ids);
        }

        [Fact]
        public async Task GetRecommendedAsync_SecondCall_IsServedFromCache()
        {
            Movie anchor = TestData.Movie("Anchor", tmdbId: "100");
            Movie other = TestData.Movie("Other", tmdbId: "300");
            (TmdbRecommendationsProvider provider, FakeHttpHandler handler) = CreateProvider(new[] { anchor, other });
            handler.Add("/movie/100/recommendations", """{"results":[{"id":300}]}""");

            var config = new PluginConfiguration { TmdbApiKey = "key" };
            await provider.GetRecommendedAsync(anchor, config, CancellationToken.None);
            int requestsAfterFirst = handler.Requests.Count;
            await provider.GetRecommendedAsync(anchor, config, CancellationToken.None);

            Assert.Equal(requestsAfterFirst, handler.Requests.Count);
        }

        private static (TmdbRecommendationsProvider Provider, FakeHttpHandler Handler) CreateProvider(
            IReadOnlyList<BaseItem> libraryItems)
        {
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager
                .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(libraryItems.ToList());

            var idLookup = new TmdbIdLookupService(libraryManager.Object, NullLogger<TmdbIdLookupService>.Instance);

            var handler = new FakeHttpHandler();
            var httpClientFactory = new Mock<IHttpClientFactory>();
            httpClientFactory
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(new HttpClient(handler));

            var provider = new TmdbRecommendationsProvider(
                httpClientFactory.Object, idLookup, NullLogger<TmdbRecommendationsProvider>.Instance);

            return (provider, handler);
        }
    }
}
