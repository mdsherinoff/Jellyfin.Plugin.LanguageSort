using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LanguageSort.Providers;

/// <summary>
/// Builds virtual language-based groupings from the Jellyfin library.
/// </summary>
public class LanguageCollectionProvider
{
    /// <summary>
    /// Group code used for items whose language could not be determined.
    /// </summary>
    public const string UnknownLanguageCode = "unknown";

    /// <summary>
    /// Display name of the unknown-language group.
    /// </summary>
    public const string UnknownLanguageName = "Unknown Language";

    /// <summary>
    /// How many episodes of a series to inspect before giving up on language detection.
    /// </summary>
    private const int EpisodeSampleSize = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly TmdbOriginalLanguageClient _tmdbClient;
    private readonly ILogger<LanguageCollectionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageCollectionProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="tmdbClient">Client for TMDb original-language lookups.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LanguageCollectionProvider}"/> interface.</param>
    public LanguageCollectionProvider(
        ILibraryManager libraryManager,
        TmdbOriginalLanguageClient tmdbClient,
        ILogger<LanguageCollectionProvider> logger)
    {
        _libraryManager = libraryManager;
        _tmdbClient = tmdbClient;
        _logger = logger;
    }

    /// <summary>
    /// Groups the library by language, ordered with pinned languages first, then
    /// alphabetically by display name.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The language groups.</returns>
    public async Task<IReadOnlyList<LanguageGroup>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Array.Empty<LanguageGroup>();
        }

        var includeItemTypes = BuildIncludeTypes(config);
        if (includeItemTypes.Length == 0)
        {
            return Array.Empty<LanguageGroup>();
        }

        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = includeItemTypes,
            Recursive = true,
            IsVirtualItem = false
        });

        _logger.LogInformation("LanguageSort: found {Count} items to group.", allItems.Count);

        // Give TMDb a fresh chance each run, even if an earlier run hit failures.
        _tmdbClient.ResetFailures();

        // Keyed by canonical language code, so "ml" and "mal" land in one group.
        var grouped = new Dictionary<string, List<BaseItem>>(StringComparer.Ordinal);

        foreach (var item in allItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var langCode = await ResolveLanguageAsync(item, config, cancellationToken).ConfigureAwait(false);
            var key = LanguageHelper.Normalize(langCode);
            if (key is null)
            {
                if (!config.ShowUnknownLanguage)
                {
                    continue;
                }

                key = UnknownLanguageCode;
            }

            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<BaseItem>();
                grouped[key] = list;
            }

            list.Add(item);
        }

        var format = config.LanguageDisplayFormat;
        var groups = grouped
            .Select(kvp => new LanguageGroup(kvp.Key, GetDisplayName(kvp.Key, format), kvp.Value))
            .ToList();

        return SortGroups(groups, config);
    }

    /// <summary>
    /// Returns the display name for a canonical language code in the given format.
    /// </summary>
    /// <param name="code">Canonical code from <see cref="LanguageHelper.Normalize"/>, or <see cref="UnknownLanguageCode"/>.</param>
    /// <param name="format">"EnglishName", "NativeName", or "IsoCode".</param>
    /// <returns>The display name.</returns>
    public static string GetDisplayName(string code, string format)
        => code == UnknownLanguageCode ? UnknownLanguageName : LanguageHelper.Resolve(code, format);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<LanguageGroup> SortGroups(List<LanguageGroup> groups, Configuration.PluginConfiguration config)
    {
        // Pinned languages first (in the order the user listed them), then alphabetical.
        var pinnedCodes = (config.PinnedLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(LanguageHelper.Normalize)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return groups
            .OrderBy(g => pinnedCodes.IndexOf(g.Code) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static BaseItemKind[] BuildIncludeTypes(Configuration.PluginConfiguration config)
    {
        var types = new List<BaseItemKind>(2);
        if (config.IncludeMovies)
        {
            types.Add(BaseItemKind.Movie);
        }

        if (config.IncludeTvShows)
        {
            types.Add(BaseItemKind.Series);
        }

        return types.ToArray();
    }

    /// <summary>
    /// Resolves an item's language, preferring the film's original language over
    /// whatever audio track the file happens to carry (which may be a dub):
    /// 1. TMDb original_language (when an API key is configured),
    /// 2. script detection on the original title,
    /// 3. audio stream tags as a last resort.
    /// </summary>
    private async Task<string?> ResolveLanguageAsync(
        BaseItem item,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        string? code = null;

        if (!string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            code = await _tmdbClient
                .GetOriginalLanguageAsync(item, config.TmdbApiKey.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (code is null && config.UseOriginalTitleFallback)
        {
            code = ScriptLanguageDetector.Detect(item.OriginalTitle)
                   ?? ScriptLanguageDetector.Detect(item.Name);
        }

        return code ?? item switch
        {
            Video video => GetAudioLanguage(video),
            Series series => GetSeriesAudioLanguage(series),
            _ => null
        };
    }

    private static string? GetSeriesAudioLanguage(Series series)
    {
        foreach (var episode in series.GetRecursiveChildren(i => i is Episode).OfType<Video>().Take(EpisodeSampleSize))
        {
            var language = GetAudioLanguage(episode);
            if (language is not null)
            {
                return language;
            }
        }

        return null;
    }

    private static string? GetAudioLanguage(Video video)
    {
        var language = video.GetMediaStreams()
            .FirstOrDefault(s => s.Type == MediaStreamType.Audio && !string.IsNullOrWhiteSpace(s.Language))
            ?.Language?.Trim().ToLowerInvariant();

        // und = undetermined, zxx = no linguistic content, mul/mis = multiple/uncoded
        return language is null or "und" or "zxx" or "mul" or "mis" ? null : language;
    }
}

/// <summary>
/// One language group: its canonical code, display name and items.
/// </summary>
/// <param name="Code">Canonical language code (stable across display formats), or "unknown".</param>
/// <param name="DisplayName">Display name in the configured format.</param>
/// <param name="Items">The items in this group.</param>
public sealed record LanguageGroup(string Code, string DisplayName, IReadOnlyList<BaseItem> Items);
