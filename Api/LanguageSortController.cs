using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LanguageSort.Providers;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LanguageSort.Api;

/// <summary>
/// API controller for Language Sort.
/// </summary>
[ApiController]
[Authorize]
[Route("LanguageSort")]
[Produces(MediaTypeNames.Application.Json)]
public class LanguageSortController : ControllerBase
{
    private readonly LanguageCollectionProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageSortController"/> class.
    /// </summary>
    public LanguageSortController(LanguageCollectionProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Returns all available language groups with item counts.
    /// GET /LanguageSort/Groups
    /// </summary>
    [HttpGet("Groups")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<LanguageGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await _provider.GetGroupsAsync(cancellationToken).ConfigureAwait(false);

        var result = groups.Select(g => new LanguageGroupDto
        {
            Language = g.DisplayName,
            Code = g.Code,
            ItemCount = g.Items.Count
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Returns the items in a specific language group, matched by display name or code.
    /// GET /LanguageSort/Items?language=French (or ?language=fr)
    /// </summary>
    [HttpGet("Items")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<LanguageItemDto>>> GetItemsForLanguageAsync(
        [Required][FromQuery] string language,
        CancellationToken cancellationToken)
    {
        var groups = await _provider.GetGroupsAsync(cancellationToken).ConfigureAwait(false);

        var code = LanguageHelper.Normalize(language);
        var group = groups.FirstOrDefault(g => string.Equals(g.DisplayName, language, StringComparison.OrdinalIgnoreCase))
                    ?? groups.FirstOrDefault(g => string.Equals(g.Code, code, StringComparison.Ordinal));
        if (group is null)
        {
            return NotFound($"No group found for language: {language}");
        }

        var result = group.Items.Select(item => MapToDto(item, group.DisplayName)).ToList();
        return Ok(result);
    }

    // ── DTO mapping ──────────────────────────────────────────────────────────

    private static LanguageItemDto MapToDto(BaseItem item, string language) => new()
    {
        Id              = item.Id,
        Name            = item.Name,
        Year            = item.ProductionYear,
        Type            = item.GetType().Name,
        Language        = language,
        HasPrimaryImage = item.HasImage(MediaBrowser.Model.Entities.ImageType.Primary, 0)
    };
}

/// <summary>DTO for a language group summary.</summary>
public class LanguageGroupDto
{
    /// <summary>Gets or sets the display name of the language.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the canonical language code (e.g. "fr"), or "unknown".</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of items in this group.</summary>
    public int ItemCount { get; set; }
}

/// <summary>DTO for a single media item.</summary>
public class LanguageItemDto
{
    /// <summary>Gets or sets item ID.</summary>
    public System.Guid Id { get; set; }

    /// <summary>Gets or sets title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets media type (Movie / Series).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the language group's display name.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the item has a primary image
    /// (fetch it via /Items/{Id}/Images/Primary).</summary>
    public bool HasPrimaryImage { get; set; }
}
