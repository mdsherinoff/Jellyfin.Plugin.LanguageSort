using System;
using System.Globalization;

namespace Jellyfin.Plugin.LanguageSort;

/// <summary>
/// Helpers for resolving ISO language codes to display names.
/// </summary>
public static class LanguageHelper
{
    /// <summary>
    /// Resolves an ISO 639-2/3-letter or 639-1/2-letter language code to a display name.
    /// Falls back to the raw code if .NET doesn't recognise it.
    /// </summary>
    /// <param name="isoCode">ISO 639 language code (2 or 3 letters).</param>
    /// <param name="format">"EnglishName", "NativeName", or "IsoCode".</param>
    /// <returns>Human-readable language name or the raw code.</returns>
    public static string Resolve(string isoCode, string format = "EnglishName")
    {
        if (string.IsNullOrWhiteSpace(isoCode))
        {
            return "Unknown";
        }

        // Normalise 3-letter codes where possible (e.g. "eng" -> "en")
        var code = isoCode.ToLowerInvariant().Trim();

        try
        {
            CultureInfo culture;
            if (code.Length == 3)
            {
                // CultureInfo doesn't directly accept ISO 639-2, but we can try
                culture = CultureInfo.GetCultureInfoByIetfLanguageTag(code)
                          ?? GetByThreeLetterCode(code);
            }
            else
            {
                culture = new CultureInfo(code);
            }

            return format switch
            {
                "NativeName" => Capitalize(culture.NativeName.Split('(')[0].Trim()),
                "IsoCode"    => culture.TwoLetterISOLanguageName.ToUpperInvariant(),
                _            => Capitalize(culture.EnglishName.Split('(')[0].Trim())
            };
        }
        catch
        {
            // Best-effort: return the raw code capitalised
            return code.ToUpperInvariant();
        }
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Attempts to find a CultureInfo by its three-letter ISO name.
    /// </summary>
    private static CultureInfo GetByThreeLetterCode(string threeLetterCode)
    {
        foreach (var ci in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            if (string.Equals(ci.ThreeLetterISOLanguageName, threeLetterCode, StringComparison.OrdinalIgnoreCase))
            {
                return ci;
            }
        }

        return CultureInfo.InvariantCulture;
    }
}
