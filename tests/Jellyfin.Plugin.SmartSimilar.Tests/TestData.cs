using System.Net;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSimilar.Tests
{
    internal static class TestData
    {
        public static Movie Movie(
            string name,
            string[]? genres = null,
            string[]? tags = null,
            string[]? studios = null,
            int? year = null,
            float? communityRating = null,
            string? officialRating = null,
            string? tmdbId = null)
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = name,
                // The SortName getter computes through server statics that do
                // not exist in unit tests - set it explicitly.
                SortName = name,
                Genres = genres ?? Array.Empty<string>(),
                Tags = tags ?? Array.Empty<string>(),
                Studios = studios ?? Array.Empty<string>(),
                ProductionYear = year,
                CommunityRating = communityRating,
                OfficialRating = officialRating
            };

            if (tmdbId != null)
            {
                movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
            }

            return movie;
        }

        public static Series Series(string name, string[]? genres = null, string? tmdbId = null)
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = name,
                SortName = name,
                Genres = genres ?? Array.Empty<string>()
            };

            if (tmdbId != null)
            {
                series.SetProviderId(MetadataProvider.Tmdb, tmdbId);
            }

            return series;
        }
    }

    /// <summary>Serves canned JSON per URL substring; records the requests it saw.</summary>
    internal sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string UrlContains, HttpStatusCode Status, string Body)> m_routes = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public void Add(string urlContains, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            m_routes.Add((urlContains, status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string url = request.RequestUri!.ToString();

            foreach ((string urlContains, HttpStatusCode status, string body) in m_routes)
            {
                if (url.Contains(urlContains, StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
