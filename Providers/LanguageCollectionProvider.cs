using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LanguageSort.Providers;

/// <summary>
/// Builds virtual language-based groupings from the Jellyfin library.
/// </summary>
public class LanguageCollectionProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LanguageCollectionProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageCollectionProvider"/> class.
    /// </summary>
    public LanguageCollectionProvider(
        ILibraryManager libraryManager,
        ILogger<LanguageCollectionProvider> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns a dictionary mapping display language name -> list of matching items.
    /// </summary>
    public Task<Dictionary<string, List<BaseItem>>> GetItemsByLanguageAsync(
        CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return Task.FromResult(new Dictionary<string, List<BaseItem>>());
        }

        var includeItemTypes = BuildIncludeTypes(config);
        if (includeItemTypes.Length == 0)
        {
            return Task.FromResult(new Dictionary<string, List<BaseItem>>());
        }

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = includeItemTypes,
            Recursive = true,
            IsVirtualItem = false
        };

        var allItems = _libraryManager.GetItemList(query);

        _logger.LogInformation("LanguageSort: found {Count} items to group.", allItems.Count);

        var grouped = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);
        var format = config.LanguageDisplayFormat;

        foreach (var item in allItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var langCode = GetPrimaryLanguageCode(item);

            string displayName;
            if (string.IsNullOrWhiteSpace(langCode))
            {
                if (!config.ShowUnknownLanguage)
                {
                    continue;
                }

                displayName = "Unknown Language";
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

        // Sort: pinned languages first, then alphabetical
        var pinnedSet = (config.PinnedLanguages ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => LanguageHelper.Resolve(c, format))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sorted = new Dictionary<string, List<BaseItem>>(StringComparer.OrdinalIgnoreCase);

        // Pinned first (preserve pinned order)
        foreach (var pinned in pinnedSet)
        {
            if (grouped.TryGetValue(pinned, out var pinnedList))
            {
                sorted[pinned] = pinnedList;
            }
        }

        // Then the rest alphabetically
        foreach (var kvp in grouped.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!sorted.ContainsKey(kvp.Key))
            {
                sorted[kvp.Key] = kvp.Value;
            }
        }

        return Task.FromResult(sorted);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static BaseItemKind[] BuildIncludeTypes(Configuration.PluginConfiguration config)
    {
        var types = new List<BaseItemKind>();
        if (config.IncludeMovies)   types.Add(BaseItemKind.Movie);
        if (config.IncludeTvShows)  types.Add(BaseItemKind.Series);
        return types.ToArray();
    }

    /// <summary>
    /// Picks the best language code for an item.
    /// Priority: OriginalLanguage > first audio stream language > null.
    /// </summary>
    private static string? GetPrimaryLanguageCode(BaseItem item)
    {
        // OriginalLanguage is set by metadata providers (TMDb, TheTVDB, etc.)
        if (!string.IsNullOrWhiteSpace(item.OriginalLanguage))
        {
            return item.OriginalLanguage;
        }

        // Fallback: first audio media stream
        if (item is Video video)
        {
            var audioStream = video.GetMediaStreams()
                .FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio
                                     && !string.IsNullOrWhiteSpace(s.Language));
            if (audioStream is not null)
            {
                return audioStream.Language;
            }
        }

        return null;
    }
}
