using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LanguageSort.Configuration;

/// <summary>
/// Plugin configuration for Language Sort.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether to include movies.
    /// </summary>
    public bool IncludeMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include TV shows.
    /// </summary>
    public bool IncludeTvShows { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to show an "Unknown Language" group
    /// for items with no language metadata.
    /// </summary>
    public bool ShowUnknownLanguage { get; set; } = true;

    /// <summary>
    /// Gets or sets the display name format for language collections.
    /// Options: "EnglishName" (e.g. "French"), "NativeName" (e.g. "Français"), "IsoCode" (e.g. "fr").
    /// </summary>
    public string LanguageDisplayFormat { get; set; } = "EnglishName";

    /// <summary>
    /// Gets or sets a comma-separated list of ISO 639-1 language codes to pin at the top.
    /// Example: "en,fr,de"
    /// </summary>
    public string PinnedLanguages { get; set; } = "en";

    /// <summary>
    /// Gets or sets a value indicating whether to guess the language from the
    /// writing system of the original title when audio streams carry no language tag.
    /// </summary>
    public bool UseOriginalTitleFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets the TMDb API key (v3). When set, items whose language can't be
    /// determined locally are looked up on TMDb using their stored TMDb id.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;
}
