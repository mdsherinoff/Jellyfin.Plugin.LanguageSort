using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LanguageSort.Providers;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LanguageSort.Tasks;

/// <summary>
/// Scheduled task that creates and maintains one Jellyfin collection per language group,
/// so the groups are browsable from any Jellyfin client under Collections.
/// Collections created by this task are tagged with a provider id, so user-created
/// collections are never touched.
/// </summary>
public class LanguageCollectionsTask : IScheduledTask
{
    /// <summary>
    /// Provider id key used to mark collections owned by this plugin.
    /// </summary>
    public const string ProviderIdKey = "LanguageSort";

    private static readonly string[] LegacyDisplayFormats = { "EnglishName", "NativeName", "IsoCode" };

    private readonly LanguageCollectionProvider _provider;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<LanguageCollectionsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageCollectionsTask"/> class.
    /// </summary>
    /// <param name="provider">The language grouping provider.</param>
    /// <param name="collectionManager">Instance of the <see cref="ICollectionManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LanguageCollectionsTask}"/> interface.</param>
    public LanguageCollectionsTask(
        LanguageCollectionProvider provider,
        ICollectionManager collectionManager,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ILogger<LanguageCollectionsTask> logger)
    {
        _provider = provider;
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update Language Collections";

    /// <inheritdoc />
    public string Key => "LanguageSortUpdateCollections";

    /// <inheritdoc />
    public string Description => "Creates and updates collections that group movies and TV shows by original language.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var groups = await _provider.GetGroupsAsync(cancellationToken).ConfigureAwait(false);

        // All collections this plugin created previously, keyed by their tag value
        // (the canonical language code; older versions used the lower-cased display name).
        // Duplicates (e.g. left over from an interrupted run) are treated as stale.
        var ownedCollections = new Dictionary<string, BoxSet>(StringComparer.OrdinalIgnoreCase);
        var staleCollections = new List<BoxSet>();

        var allBoxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true
        });

        foreach (var boxSet in allBoxSets.OfType<BoxSet>())
        {
            var tagValue = boxSet.GetProviderId(ProviderIdKey);
            if (!string.IsNullOrEmpty(tagValue) && !ownedCollections.TryAdd(tagValue, boxSet))
            {
                staleCollections.Add(boxSet);
            }
        }

        var processed = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var collection = await TakeOwnedCollectionAsync(ownedCollections, group.Code, cancellationToken).ConfigureAwait(false);
            if (collection is not null)
            {
                await UpdateCollectionAsync(collection, group.Items).ConfigureAwait(false);
            }
            else
            {
                collection = await CreateCollectionAsync(group.DisplayName, group.Code, group.Items).ConfigureAwait(false);
            }

            await ApplyNamingAsync(collection, group.DisplayName, cancellationToken).ConfigureAwait(false);
            await EnsureImageAsync(collection, group.DisplayName, cancellationToken).ConfigureAwait(false);

            processed++;
            progress.Report(processed * 100.0 / groups.Count);
        }

        // Whatever is left no longer corresponds to a language group
        // (e.g. media removed, or "Unknown Language" was turned off).
        staleCollections.AddRange(ownedCollections.Values);
        foreach (var stale in staleCollections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("LanguageSort: removing stale collection {Name}.", stale.Name);
            _libraryManager.DeleteItem(stale, new DeleteOptions { DeleteFileLocation = true }, true);
        }

        progress.Report(100);
    }

    /// <summary>
    /// Finds and removes the existing collection for a language from <paramref name="owned"/>.
    /// Collections created before v1.0.7 are tagged with the lower-cased display name in
    /// whatever format was active then; those are matched by every possible display name
    /// and re-tagged with the language code, so they keep their id, image and favorites.
    /// </summary>
    private static async Task<BoxSet?> TakeOwnedCollectionAsync(
        Dictionary<string, BoxSet> owned,
        string code,
        CancellationToken cancellationToken)
    {
        if (owned.Remove(code, out var existing))
        {
            return existing;
        }

        foreach (var format in LegacyDisplayFormats)
        {
            var legacyTag = LanguageCollectionProvider.GetDisplayName(code, format).ToLowerInvariant();
            if (owned.Remove(legacyTag, out existing))
            {
                existing.SetProviderId(ProviderIdKey, code);
                await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                return existing;
            }
        }

        return null;
    }

    private async Task<BoxSet> CreateCollectionAsync(string displayName, string code, IReadOnlyList<BaseItem> items)
    {
        _logger.LogInformation("LanguageSort: creating collection {Name} with {Count} items.", displayName, items.Count);

        return await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = displayName,
            IsLocked = true,
            ProviderIds = new Dictionary<string, string> { [ProviderIdKey] = code },
            ItemIdList = items.Select(i => i.Id.ToString("N")).ToArray()
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the collection's display name and sort title in line with the settings:
    /// an optional visible prefix, and a forced sort title starting with "!" that
    /// pins language collections before other collections when sorting by name.
    /// </summary>
    private static async Task ApplyNamingAsync(BoxSet collection, string displayName, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        var prefix = config.CollectionNamePrefix?.Trim();
        var desiredName = string.IsNullOrEmpty(prefix) ? displayName : $"{prefix} {displayName}";
        var desiredSortName = config.SortLanguageCollectionsFirst ? "!" + displayName : null;

        var changed = false;
        if (!string.Equals(collection.Name, desiredName, StringComparison.Ordinal))
        {
            collection.Name = desiredName;
            changed = true;
        }

        if (!string.Equals(collection.ForcedSortName, desiredSortName, StringComparison.Ordinal))
        {
            collection.ForcedSortName = desiredSortName;
            changed = true;
        }

        if (changed)
        {
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generates and saves a poster for the collection unless it already has one
    /// (a user-provided image is never overwritten). The poster shows the plain
    /// language name, without any configured name prefix.
    /// </summary>
    private async Task EnsureImageAsync(BoxSet collection, string label, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.GenerateCollectionImages != true || collection.HasImage(ImageType.Primary, 0))
        {
            return;
        }

        try
        {
            using var poster = CollectionImageGenerator.GeneratePoster(label);
            await _providerManager
                .SaveImage(collection, poster, "image/png", ImageType.Primary, null, cancellationToken)
                .ConfigureAwait(false);
            await collection
                .UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Image generation must never fail the sync itself.
            _logger.LogWarning(ex, "LanguageSort: could not generate image for collection {Name}.", collection.Name);
        }
    }

    private async Task UpdateCollectionAsync(BoxSet collection, IReadOnlyList<BaseItem> items)
    {
        var wantedIds = items.Select(i => i.Id).ToHashSet();
        var currentIds = collection.GetLinkedChildren().Select(i => i.Id).ToHashSet();

        var toAdd = wantedIds.Except(currentIds).ToList();
        var toRemove = currentIds.Except(wantedIds).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "LanguageSort: updating collection {Name} (+{Added}/-{Removed} items).",
            collection.Name,
            toAdd.Count,
            toRemove.Count);

        if (toAdd.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(collection.Id, toAdd).ConfigureAwait(false);
        }

        if (toRemove.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, toRemove).ConfigureAwait(false);
        }
    }
}
