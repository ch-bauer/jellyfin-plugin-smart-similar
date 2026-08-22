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
        private readonly LocalScoringProvider m_localScoring;
        private readonly TmdbRecommendationsProvider m_tmdbProvider;

        public SmartSimilarController(
            ILibraryManager libraryManager,
            SimilarItemsService similarItemsService,
            IHttpClientFactory httpClientFactory,
            LocalScoringProvider localScoring,
            TmdbRecommendationsProvider tmdbProvider)
        {
            m_libraryManager = libraryManager;
            m_similarItemsService = similarItemsService;
            m_httpClientFactory = httpClientFactory;
            m_localScoring = localScoring;
            m_tmdbProvider = tmdbProvider;
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

        /// <summary>
        /// Scores the library against <b>several</b> anchors at once and returns the
        /// numbers, not just an order. GET SmartSimilar/Items throws the scores away,
        /// which forces a caller with more than one seed to fuse rankings blindly and
        /// leaves it unable to say why anything was picked.
        /// </summary>
        /// <remarks>
        /// It honours the configured provider. TMDb returns an order and no numbers, so
        /// its rank is mapped onto the same 0-100 scale - see <see cref="TmdbBlend"/> for
        /// the two rules. The local pass runs whatever the provider is, because it is
        /// what knows <em>why</em> two titles are alike; under "Tmdb" it supplies the
        /// shared signals while TMDb supplies the score. No collection-sibling or
        /// watched-item exclusions are applied here, and seeds are never returned as
        /// results. The caller renders the items through the standard /Items API, which
        /// is what enforces library access, so only ids are exposed.
        /// </remarks>
        /// <param name="itemIds">Comma-separated anchor item ids.</param>
        /// <param name="userId">The user whose library access limits the candidates.</param>
        /// <param name="minScore">Floor on the mean score; defaults to the configured MinScore.</param>
        /// <param name="limit">Maximum results; defaults to 50, capped at 500.</param>
        /// <param name="provider">Overrides the configured provider for this request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The seeds as understood, and the ranked candidates with their scores.</returns>
        [HttpGet("Score")]
        [Authorize]
        public async Task<ActionResult<ScoreResponse>> GetScore(
            [FromQuery] string? itemIds,
            [FromQuery] Guid userId,
            [FromQuery] int? minScore,
            [FromQuery] int? limit,
            [FromQuery] string? provider,
            CancellationToken cancellationToken)
        {
            List<Guid> requested = ParseIds(itemIds);
            if (requested.Count == 0)
            {
                return BadRequest("itemIds is required.");
            }

            PluginConfiguration config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            string wanted = Normalize(provider) ?? Normalize(config.Provider) ?? "Local";
            double floor = minScore ?? config.MinScore;
            int max = Math.Clamp(limit ?? 50, 1, 500);

            List<ScoreSeedDto> seeds = new List<ScoreSeedDto>(requested.Count);
            List<ScoreAggregator.SeedScores> scores = new List<ScoreAggregator.SeedScores>();
            HashSet<Guid> excluded = new HashSet<Guid>(requested);

            for (int index = 0; index < requested.Count; index++)
            {
                BaseItem? item = m_libraryManager.GetItemById(requested[index]);
                if (item is not Movie && item is not Series)
                {
                    seeds.Add(new ScoreSeedDto(
                        requested[index], item?.Name ?? string.Empty, string.Empty, false, "None", 0));
                    continue;
                }

                string kind = item is Series ? "Series" : "Movie";

                // Scored with no floor of their own: the floor belongs on the mean, or a
                // title that matches every seed a little would be dropped seed by seed
                // before it could ever be averaged.
                IReadOnlyList<ScoredCandidate> local = m_localScoring.GetScoredDetailed(item, userId, 0);

                IReadOnlyList<Guid> tmdb = wanted == "Local"
                    ? Array.Empty<Guid>()
                    : await m_tmdbProvider.GetRecommendedAsync(item, config, cancellationToken).ConfigureAwait(false);

                // A seed TMDb cannot answer for - no key, no TMDb id, nothing of its
                // list in this library - falls back to local rather than dropping out,
                // and the seed says which one actually answered.
                string source = tmdb.Count == 0 ? "Local" : wanted;

                seeds.Add(new ScoreSeedDto(item.Id, item.Name ?? string.Empty, kind, true, source, tmdb.Count));
                scores.Add(new ScoreAggregator.SeedScores(index, kind, ApplyProvider(local, tmdb, source)));
            }

            IReadOnlyList<ScoredItemDto> results = ScoreAggregator.Merge(
                scores, requested.Count, excluded, floor, max);

            return Ok(new ScoreResponse(scores.Count > 0, seeds, results));
        }

        /// <summary>
        /// Rescores one seed's local results the way the provider asks. "Tmdb" keeps only
        /// what TMDb named and scores it by rank; "Hybrid" lifts those and leaves the rest
        /// on their local score; "Local" changes nothing. The shared signals survive every
        /// route, because they are the only thing that can explain a suggestion.
        /// </summary>
        private static IReadOnlyList<ScoredCandidate> ApplyProvider(
            IReadOnlyList<ScoredCandidate> local, IReadOnlyList<Guid> tmdb, string source)
        {
            if (source == "Local" || tmdb.Count == 0)
            {
                return local;
            }

            Dictionary<Guid, int> ranks = new Dictionary<Guid, int>(tmdb.Count);
            for (int rank = 0; rank < tmdb.Count; rank++)
            {
                ranks.TryAdd(tmdb[rank], rank);
            }

            List<ScoredCandidate> rescored = new List<ScoredCandidate>(local.Count);
            HashSet<Guid> seen = new HashSet<Guid>();

            foreach (ScoredCandidate candidate in local)
            {
                int rank = ranks.TryGetValue(candidate.Id, out int found) ? found : TmdbBlend.NotRecommended;

                if (source == "Tmdb")
                {
                    if (rank == TmdbBlend.NotRecommended)
                    {
                        continue;
                    }

                    rescored.Add(candidate with { Score = TmdbBlend.RankScore(rank, tmdb.Count) });
                }
                else
                {
                    rescored.Add(candidate with { Score = TmdbBlend.Hybrid(candidate.Score, rank, tmdb.Count) });
                }

                seen.Add(candidate.Id);
            }

            // A TMDb recommendation the local pass never saw - the other media kind, or
            // an item outside this user's candidate list - is still worth returning under
            // Tmdb, with no shared signals to show for it.
            if (source == "Tmdb")
            {
                for (int rank = 0; rank < tmdb.Count; rank++)
                {
                    if (seen.Add(tmdb[rank]))
                    {
                        rescored.Add(new ScoredCandidate(
                            tmdb[rank], TmdbBlend.RankScore(rank, tmdb.Count), SharedSignals.None));
                    }
                }
            }

            rescored.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            return rescored;
        }

        private static string? Normalize(string? provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return null;
            }

            return provider.Trim().ToLowerInvariant() switch
            {
                "tmdb" => "Tmdb",
                "hybrid" => "Hybrid",
                _ => "Local"
            };
        }

        private static List<Guid> ParseIds(string? itemIds)
        {
            List<Guid> ids = new List<Guid>();
            if (string.IsNullOrWhiteSpace(itemIds))
            {
                return ids;
            }

            foreach (string part in itemIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Jellyfin hands guids out both dashed and dash-less; Guid.TryParse
                // takes either, and a duplicate seed would double its own weight.
                if (Guid.TryParse(part, out Guid id) && id != Guid.Empty && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
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
