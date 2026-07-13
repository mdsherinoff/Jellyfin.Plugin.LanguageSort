using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LanguageSort.Providers;

/// <summary>
/// Fetches an item's original language from the TMDb API, using the TMDb id
/// that Jellyfin stored when the item was matched. Results are cached for the
/// lifetime of the server process.
/// </summary>
public class TmdbOriginalLanguageClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbOriginalLanguageClient> _logger;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private int _failureCount;

    /// <summary>
    /// Stop calling TMDb for the rest of the run after this many consecutive failures
    /// (wrong API key, network down), so a misconfiguration doesn't produce hundreds of errors.
    /// </summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbOriginalLanguageClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TmdbOriginalLanguageClient}"/> interface.</param>
    public TmdbOriginalLanguageClient(
        IHttpClientFactory httpClientFactory,
        ILogger<TmdbOriginalLanguageClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the ISO 639-1 original language of the item according to TMDb,
    /// or <c>null</c> when the item has no TMDb id, the key is invalid, or the call fails.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="apiKey">TMDb API key (v3).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The original language code or <c>null</c>.</returns>
    public async Task<string?> GetOriginalLanguageAsync(BaseItem item, string apiKey, CancellationToken cancellationToken)
    {
        var tmdbId = item.GetProviderId(MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdbId) || Volatile.Read(ref _failureCount) >= MaxConsecutiveFailures)
        {
            return null;
        }

        var kind = item is Series ? "tv" : "movie";
        var cacheKey = $"{kind}:{tmdbId}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var url = new Uri($"https://api.themoviedb.org/3/{kind}/{Uri.EscapeDataString(tmdbId)}?api_key={Uri.EscapeDataString(apiKey)}");

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _failureCount);
                _logger.LogWarning(
                    "LanguageSort: TMDb lookup for {Kind} {TmdbId} failed with {Status}. Check the API key in the plugin settings.",
                    kind,
                    tmdbId,
                    response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            string? language = null;
            if (doc.RootElement.TryGetProperty("original_language", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                language = prop.GetString();
                if (string.IsNullOrWhiteSpace(language))
                {
                    language = null;
                }
            }

            Volatile.Write(ref _failureCount, 0);
            _cache[cacheKey] = language;
            return language;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            _logger.LogWarning(ex, "LanguageSort: TMDb lookup for {Kind} {TmdbId} failed.", kind, tmdbId);
            return null;
        }
    }
}
