using Jellyfin.Plugin.SmartSimilar.Configuration;
using Jellyfin.Plugin.SmartSimilar.Model;
using Jellyfin.Plugin.SmartSimilar.Services;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSimilar.Controllers
{
    [ApiController]
    [Route("SmartSimilar")]
    public class SmartSimilarController : ControllerBase
    {
        private readonly ILibraryManager m_libraryManager;
        private readonly SimilarItemsService m_similarItemsService;
        private readonly IHttpClientFactory m_httpClientFactory;

        public SmartSimilarController(
            ILibraryManager libraryManager,
            SimilarItemsService similarItemsService,
            IHttpClientFactory httpClientFactory)
        {
            m_libraryManager = libraryManager;
            m_similarItemsService = similarItemsService;
            m_httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Returns the ranked similar item ids for an item. Active=false means the
        /// item type is not handled and the native section should stay untouched.
        /// The client renders the items through the standard /Items API (which
        /// enforces the user's library access), so this endpoint only exposes ids.
        /// </summary>
        [HttpGet("Items")]
        [Authorize]
        public async Task<ActionResult<SimilarItemsResponse>> GetItems(
            [FromQuery] Guid itemId, [FromQuery] Guid userId, CancellationToken cancellationToken)
        {
            if (itemId == Guid.Empty)
            {
                return BadRequest("itemId is required.");
            }

            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            BaseItem? item = m_libraryManager.GetItemById(itemId);
            if (item is not Movie && item is not Series)
            {
                return Ok(new SimilarItemsResponse(false, Array.Empty<Guid>()));
            }

            IReadOnlyList<Guid> ids = await m_similarItemsService
                .GetSimilarAsync(item, userId, config, cancellationToken)
                .ConfigureAwait(false);

            return Ok(new SimilarItemsResponse(true, ids));
        }

        /// <summary>Validates a TMDb API key against the TMDb configuration endpoint.</summary>
        [HttpPost("TestTmdbKey")]
        [Authorize(Policy = "RequiresElevation")]
        public async Task<ActionResult<TmdbKeyTestResponse>> TestTmdbKey(
            [FromBody] TmdbKeyTestRequest request, CancellationToken cancellationToken)
        {
            string apiKey = request.ApiKey?.Trim() ?? string.Empty;
            if (apiKey.Length == 0)
            {
                return Ok(new TmdbKeyTestResponse(false, "No API key entered."));
            }

            try
            {
                using HttpRequestMessage httpRequest = TmdbRecommendationsProvider.CreateRequest(
                    "https://api.themoviedb.org/3/configuration", apiKey);
                HttpClient client = m_httpClientFactory.CreateClient(NamedClient.Default);

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using HttpResponseMessage response = await client.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return Ok(new TmdbKeyTestResponse(true, "The key is valid."));
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return Ok(new TmdbKeyTestResponse(false, "TMDb rejected the key (401 Unauthorized)."));
                }

                return Ok(new TmdbKeyTestResponse(false, $"TMDb answered with status {(int)response.StatusCode}."));
            }
            catch (Exception ex)
            {
                return Ok(new TmdbKeyTestResponse(false, "Request failed: " + ex.Message));
            }
        }
    }
}
