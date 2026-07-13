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

    private readonly LanguageCollectionProvider _provider;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LanguageCollectionsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageCollectionsTask"/> class.
    /// </summary>
    /// <param name="provider">The language grouping provider.</param>
    /// <param name="collectionManager">Instance of the <see cref="ICollectionManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LanguageCollectionsTask}"/> interface.</param>
    public LanguageCollectionsTask(
        LanguageCollectionProvider provider,
        ICollectionManager collectionManager,
        ILibraryManager libraryManager,
        ILogger<LanguageCollectionsTask> logger)
    {
        _provider = provider;
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update Language Collections";

    /// <inheritdoc />
    public string Key => "LanguageSortUpdateCollections";

    /// <inheritdoc />
    public string Description => "Creates and updates collections that group movies and TV shows by audio language.";

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
        var grouped = _provider.GetItemsByLanguage(cancellationToken);

        // All collections this plugin created previously, keyed by their tag value.
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
        foreach (var (displayName, items) in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tag = displayName.ToLowerInvariant();
            if (ownedCollections.Remove(tag, out var existing))
            {
                await UpdateCollectionAsync(existing, items).ConfigureAwait(false);
            }
            else
            {
                await CreateCollectionAsync(displayName, tag, items).ConfigureAwait(false);
            }

            processed++;
            progress.Report(processed * 100.0 / grouped.Count);
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

    private async Task CreateCollectionAsync(string displayName, string tag, IReadOnlyList<BaseItem> items)
    {
        _logger.LogInformation("LanguageSort: creating collection {Name} with {Count} items.", displayName, items.Count);

        await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = displayName,
            IsLocked = true,
            ProviderIds = new Dictionary<string, string> { [ProviderIdKey] = tag },
            ItemIdList = items.Select(i => i.Id.ToString("N")).ToArray()
        }).ConfigureAwait(false);
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
