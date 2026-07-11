using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Jellyfin.Plugin.SmartSimilar.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSimilar.Services
{
    /// <summary>
    /// Fetches TMDb recommendations for an item and maps them to local library
    /// items. Results (already mapped to local ids) are cached for a day. Any
    /// failure returns an empty list - the orchestrator falls back to local scoring.
    /// </summary>
    public sealed class TmdbRecommendationsProvider
    {
        private static readonly TimeSpan s_cacheTtl = TimeSpan.FromHours(24);
        private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(10);
        private const int MaxCacheEntries = 500;

        private readonly IHttpClientFactory m_httpClientFactory;
        private readonly TmdbIdLookupService m_tmdbIdLookup;
        private readonly ILogger<TmdbRecommendationsProvider> m_logger;
        private readonly ConcurrentDictionary<string, CacheEntry> m_cache = new();

        private sealed record CacheEntry(DateTime FetchedAt, IReadOnlyList<Guid> Ids);

        public TmdbRecommendationsProvider(
            IHttpClientFactory httpClientFactory,
            TmdbIdLookupService tmdbIdLookup,
            ILogger<TmdbRecommendationsProvider> logger)
        {
            m_httpClientFactory = httpClientFactory;
            m_tmdbIdLookup = tmdbIdLookup;
            m_logger = logger;
        }

        public async Task<IReadOnlyList<Guid>> GetRecommendedAsync(
            BaseItem anchor, PluginConfiguration config, CancellationToken cancellationToken)
        {
            string apiKey = config.TmdbApiKey?.Trim() ?? string.Empty;
            if (apiKey.Length == 0)
            {
                m_logger.LogDebug("No TMDb API key configured.");
                return Array.Empty<Guid>();
            }

            if (!anchor.TryGetProviderId(MetadataProvider.Tmdb, out string? tmdbId) || string.IsNullOrEmpty(tmdbId))
            {
                m_logger.LogDebug("Item {Name} has no TMDb id.", anchor.Name);
                return Array.Empty<Guid>();
            }

            bool isSeries = anchor is Series;
            string mediaType = isSeries ? "tv" : "movie";
            string cacheKey = mediaType + ":" + tmdbId;

            if (m_cache.TryGetValue(cacheKey, out CacheEntry? cached)
                && DateTime.UtcNow - cached.FetchedAt < s_cacheTtl)
            {
                return cached.Ids;
            }

            try
            {
                List<Guid> mapped = new List<Guid>();
                HashSet<Guid> seen = new HashSet<Guid> { anchor.Id };

                List<long> recommended = await FetchResultIdsAsync(
                    $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}/recommendations?page=1",
                    apiKey, cancellationToken).ConfigureAwait(false);

                // Sparse recommendations (obscure titles): TMDb's "similar" endpoint as backup.
                if (recommended.Count == 0)
                {
                    recommended = await FetchResultIdsAsync(
                        $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}/similar?page=1",
                        apiKey, cancellationToken).ConfigureAwait(false);
                }

                MapToLibrary(recommended, isSeries, mapped, seen);

                // Only a fraction of TMDb's suggestions exist in the library; a second
                // page improves the hit count when the first page mapped too few.
                if (recommended.Count > 0 && mapped.Count < config.ResultLimit)
                {
                    List<long> page2 = await FetchResultIdsAsync(
                        $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}/recommendations?page=2",
                        apiKey, cancellationToken).ConfigureAwait(false);
                    MapToLibrary(page2, isSeries, mapped, seen);
                }

                StoreInCache(cacheKey, mapped);
                m_logger.LogDebug("TMDb recommendations for {Name}: {Fetched} fetched, {Mapped} in library.",
                    anchor.Name, recommended.Count, mapped.Count);
                return mapped;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                m_logger.LogWarning(ex, "TMDb request for {Name} failed; falling back to local scoring.", anchor.Name);
                return Array.Empty<Guid>();
            }
        }

        /// <summary>Builds a TMDb request with the right auth style for the given key.</summary>
        public static HttpRequestMessage CreateRequest(string url, string apiKey)
        {
            HttpRequestMessage request;

            // v4 read access tokens are JWTs ("ey..."); v3 keys go into the query string.
            if (apiKey.StartsWith("ey", StringComparison.Ordinal))
            {
                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                string separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
                request = new HttpRequestMessage(HttpMethod.Get, url + separator + "api_key=" + Uri.EscapeDataString(apiKey));
            }

            return request;
        }

        private async Task<List<long>> FetchResultIdsAsync(string url, string apiKey, CancellationToken cancellationToken)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(s_requestTimeout);

            using HttpRequestMessage request = CreateRequest(url, apiKey);
            HttpClient client = m_httpClientFactory.CreateClient(NamedClient.Default);

            using HttpResponseMessage response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            List<long> ids = new List<long>();
            if (document.RootElement.TryGetProperty("results", out JsonElement results)
                && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement result in results.EnumerateArray())
                {
                    if (result.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number)
                    {
                        ids.Add(id.GetInt64());
                    }
                }
            }

            return ids;
        }

        private void MapToLibrary(List<long> tmdbIds, bool isSeries, List<Guid> mapped, HashSet<Guid> seen)
        {
            foreach (long tmdbId in tmdbIds)
            {
                string key = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Guid? localId = isSeries ? m_tmdbIdLookup.FindSeries(key) : m_tmdbIdLookup.FindMovie(key);

                if (localId.HasValue && seen.Add(localId.Value))
                {
                    mapped.Add(localId.Value);
                }
            }
        }

        private void StoreInCache(string cacheKey, List<Guid> ids)
        {
            if (m_cache.Count >= MaxCacheEntries)
            {
                // Drop the oldest entry; precise LRU is not worth the bookkeeping here.
                string? oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (KeyValuePair<string, CacheEntry> entry in m_cache)
                {
                    if (entry.Value.FetchedAt < oldest)
                    {
                        oldest = entry.Value.FetchedAt;
                        oldestKey = entry.Key;
                    }
                }

                if (oldestKey != null)
                {
                    m_cache.TryRemove(oldestKey, out _);
                }
            }

            m_cache[cacheKey] = new CacheEntry(DateTime.UtcNow, ids);
        }
    }
}
