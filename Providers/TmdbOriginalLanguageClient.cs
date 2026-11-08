using System;
using System.Collections.Concurrent;
using System.Net;
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
    /// <summary>
    /// Stop calling TMDb for the rest of the run after this many consecutive failures
    /// (wrong API key, network down), so a misconfiguration doesn't produce hundreds of errors.
    /// </summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// How many times a rate-limited (HTTP 429) request is retried before giving up.
    /// </summary>
    private const int MaxRateLimitRetries = 3;

    /// <summary>
    /// Upper bound on how long to honour a Retry-After header.
    /// </summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbOriginalLanguageClient> _logger;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private int _failureCount;

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
    /// Re-enables TMDb lookups after earlier failures. Called at the start of each
    /// grouping run, so a transient outage doesn't disable TMDb until the server restarts.
    /// </summary>
    public void ResetFailures() => Volatile.Write(ref _failureCount, 0);

    /// <summary>
    /// Returns the ISO 639 original language of the item according to TMDb,
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

            for (var attempt = 0; ; attempt++)
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                    if (delay > MaxRetryDelay)
                    {
                        delay = MaxRetryDelay;
                    }

                    _logger.LogDebug("LanguageSort: TMDb rate limit hit, retrying in {Delay}.", delay);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // The id no longer exists on TMDb. The key and network are fine, so this
                    // is not a failure; remember the miss and fall back to local detection.
                    Volatile.Write(ref _failureCount, 0);
                    _cache[cacheKey] = null;
                    _logger.LogDebug("LanguageSort: TMDb has no {Kind} with id {TmdbId}.", kind, tmdbId);
                    return null;
                }

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
                    language = NormalizeTmdbLanguage(prop.GetString());
                }

                Volatile.Write(ref _failureCount, 0);
                _cache[cacheKey] = language;
                return language;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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

    /// <summary>
    /// Maps TMDb's language values onto ISO 639. TMDb is ISO 639-1 except for
    /// two non-standard codes: "cn" (Cantonese) and "xx" (no language).
    /// </summary>
    /// <param name="language">The raw <c>original_language</c> value.</param>
    /// <returns>An ISO 639 code, or <c>null</c> when TMDb gives no usable language.</returns>
    internal static string? NormalizeTmdbLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        return language.Trim().ToLowerInvariant() switch
        {
            "cn" => "yue",
            "xx" => null,
            var code => code
        };
    }
}
