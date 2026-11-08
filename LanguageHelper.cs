using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.LanguageSort;

/// <summary>
/// Helpers for resolving ISO language codes to display names.
/// </summary>
public static class LanguageHelper
{
    private static readonly ConcurrentDictionary<string, CultureInfo?> CultureCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps ISO 639-2/B (bibliographic) codes to their 639-2/T (terminology) equivalent,
    /// which is what <see cref="CultureInfo.ThreeLetterISOLanguageName"/> reports.
    /// </summary>
    private static readonly Dictionary<string, string> BibliographicToTerminology = new(StringComparer.Ordinal)
    {
        ["alb"] = "sqi",
        ["arm"] = "hye",
        ["baq"] = "eus",
        ["bur"] = "mya",
        ["chi"] = "zho",
        ["cze"] = "ces",
        ["dut"] = "nld",
        ["fre"] = "fra",
        ["geo"] = "kat",
        ["ger"] = "deu",
        ["gre"] = "ell",
        ["ice"] = "isl",
        ["mac"] = "mkd",
        ["may"] = "msa",
        ["per"] = "fas",
        ["rum"] = "ron",
        ["slo"] = "slk",
        ["tib"] = "bod",
        ["wel"] = "cym",
    };

    /// <summary>
    /// Returns a canonical, display-format-independent key for a language code, so
    /// that e.g. "ml", "mal" and "ml-IN" all map to the same group: the 2-letter code
    /// when one exists, otherwise the 3-letter code. Unrecognised codes are returned
    /// lower-cased. Returns <c>null</c> for an empty code.
    /// </summary>
    /// <param name="isoCode">ISO 639 language code (2 or 3 letters), optionally with a region subtag.</param>
    /// <returns>The canonical key, or <c>null</c>.</returns>
    public static string? Normalize(string? isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode))
        {
            return null;
        }

        var code = isoCode.Trim().ToLowerInvariant();
        var culture = CultureCache.GetOrAdd(code, FindCulture);
        return culture is null ? code : culture.TwoLetterISOLanguageName.ToLowerInvariant();
    }

    /// <summary>
    /// Resolves an ISO 639-1 (2-letter) or ISO 639-2 (3-letter) language code to a display name.
    /// Falls back to the raw code (upper-cased) if the code is not recognised.
    /// </summary>
    /// <param name="isoCode">ISO 639 language code (2 or 3 letters), optionally with a region subtag.</param>
    /// <param name="format">"EnglishName", "NativeName", or "IsoCode".</param>
    /// <returns>Human-readable language name or the raw code.</returns>
    public static string Resolve(string isoCode, string format = "EnglishName")
    {
        if (string.IsNullOrWhiteSpace(isoCode))
        {
            return "Unknown";
        }

        var code = isoCode.Trim().ToLowerInvariant();
        var culture = CultureCache.GetOrAdd(code, FindCulture);
        if (culture is null)
        {
            return code.ToUpperInvariant();
        }

        return format switch
        {
            "NativeName" => Capitalize(StripRegion(culture.NativeName)),
            "IsoCode" => culture.TwoLetterISOLanguageName.ToUpperInvariant(),
            _ => Capitalize(StripRegion(culture.EnglishName))
        };
    }

    private static CultureInfo? FindCulture(string code)
    {
        // "pt-br" -> language part only; we group by language, not locale.
        var dashIndex = code.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex > 0)
        {
            code = code[..dashIndex];
        }

        if (code.Length == 3)
        {
            return FindByThreeLetterCode(code);
        }

        try
        {
            // predefinedOnly avoids ICU synthesizing a culture for made-up codes.
            var culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            return culture.IsNeutralCulture || !string.IsNullOrEmpty(culture.Name) ? culture : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static CultureInfo? FindByThreeLetterCode(string threeLetterCode)
    {
        if (BibliographicToTerminology.TryGetValue(threeLetterCode, out var terminology))
        {
            threeLetterCode = terminology;
        }

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (string.Equals(culture.ThreeLetterISOLanguageName, threeLetterCode, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(culture.Name))
            {
                return culture;
            }
        }

        // Some languages (e.g. "yue", Cantonese) have no neutral culture whose
        // ThreeLetterISOLanguageName matches, but can be looked up directly.
        try
        {
            var culture = CultureInfo.GetCultureInfo(threeLetterCode, predefinedOnly: true);
            return string.IsNullOrEmpty(culture.Name) ? null : culture;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string StripRegion(string name)
    {
        var parenIndex = name.IndexOf('(', StringComparison.Ordinal);
        return (parenIndex > 0 ? name[..parenIndex] : name).Trim();
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
