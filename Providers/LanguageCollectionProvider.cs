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
    private const string UnknownLanguageName = "Unknown Language";

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
    /// Returns a dictionary mapping display language name -> list of matching items,
    /// ordered with pinned languages first, then alphabetically.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Grouped library items keyed by language display name.</returns>
    public async Task<Dictionary<string, List<BaseItem>>> GetItemsByLanguageAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        }

        var includeItemTypes = BuildIncludeTypes(config);
        if (includeItemTypes.Length == 0)
        {
            return new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        }

        var allItems = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = includeItemTypes,
            Recursive = true,
            IsVirtualItem = false
        });

        _logger.LogInformation("LanguageSort: found {Count} items to group.", allItems.Count);

        var grouped = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        var format = config.LanguageDisplayFormat;

        foreach (var item in allItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var langCode = await ResolveLanguageAsync(item, config, cancellationToken).ConfigureAwait(false);

            string displayName;
            if (string.IsNullOrWhiteSpace(langCode))
            {
                if (!config.ShowUnknownLanguage)
                {
                    continue;
                }

                displayName = UnknownLanguageName;
            }
            else
            {
                displayName = LanguageHelper.Resolve(langCode, format);
            }

            if (!grouped.TryGetValue(displayName, out var list))
            {
                list = new List<BaseItem>();
                grouped[displayName] = list;
            }

            list.Add(item);
        }

        return SortGroups(grouped, config, format);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, List<BaseItem>> SortGroups(
        Dictionary<string, List<BaseItem>> grouped,
        Configuration.PluginConfiguration config,
        string format)
    {
        // Pinned languages first (in the order the user listed them), then alphabetical.
        var pinnedNames = (config.PinnedLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => LanguageHelper.Resolve(code, format))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sorted = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pinned in pinnedNames)
        {
            if (grouped.TryGetValue(pinned, out var pinnedList))
            {
                sorted[pinned] = pinnedList;
            }
        }

        foreach (var kvp in grouped.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!sorted.ContainsKey(kvp.Key))
            {
                sorted[kvp.Key] = kvp.Value;
            }
        }

        return sorted;
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
