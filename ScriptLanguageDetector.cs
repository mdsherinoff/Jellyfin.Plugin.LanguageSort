using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LanguageSort;

/// <summary>
/// Guesses a language from the writing system (Unicode script) of a title.
/// Used as a fallback when media files carry no audio language tags.
/// Latin-script text is intentionally inconclusive: many languages share it.
/// </summary>
public static class ScriptLanguageDetector
{
    /// <summary>
    /// Minimum number of script-specific characters required before trusting a guess.
    /// </summary>
    private const int MinimumMatches = 2;

    private static readonly (int Start, int End, string Language)[] ScriptRanges =
    {
        // Indic scripts — each maps to one dominant language.
        (0x0900, 0x097F, "hi"), // Devanagari (Hindi; also Marathi/Nepali)
        (0x0980, 0x09FF, "bn"), // Bengali
        (0x0A00, 0x0A7F, "pa"), // Gurmukhi (Punjabi)
        (0x0A80, 0x0AFF, "gu"), // Gujarati
        (0x0B00, 0x0B7F, "or"), // Odia
        (0x0B80, 0x0BFF, "ta"), // Tamil
        (0x0C00, 0x0C7F, "te"), // Telugu
        (0x0C80, 0x0CFF, "kn"), // Kannada
        (0x0D00, 0x0D7F, "ml"), // Malayalam
        (0x0D80, 0x0DFF, "si"), // Sinhala

        // East Asian. Kana/Hangul are checked before Han so that Japanese
        // titles containing kanji are not misread as Chinese.
        (0x3040, 0x30FF, "ja"), // Hiragana + Katakana
        (0x1100, 0x11FF, "ko"), // Hangul Jamo
        (0x3130, 0x318F, "ko"), // Hangul compatibility Jamo
        (0xAC00, 0xD7AF, "ko"), // Hangul syllables
        (0x4E00, 0x9FFF, "zh"), // CJK Unified Ideographs (Han)

        // Other scripts with a single dominant language.
        (0x0400, 0x04FF, "ru"), // Cyrillic (Russian; also other Slavic)
        (0x0370, 0x03FF, "el"), // Greek
        (0x0590, 0x05FF, "he"), // Hebrew
        (0x0600, 0x077F, "ar"), // Arabic (also Persian/Urdu)
        (0x0E00, 0x0E7F, "th"), // Thai
        (0x0E80, 0x0EFF, "lo"), // Lao
        (0x1000, 0x109F, "my"), // Myanmar
        (0x1200, 0x137F, "am"), // Ethiopic (Amharic)
        (0x10A0, 0x10FF, "ka"), // Georgian
        (0x0530, 0x058F, "hy"), // Armenian
        (0x1780, 0x17FF, "km"), // Khmer
    };

    /// <summary>
    /// Attempts to determine the language of <paramref name="text"/> from its script.
    /// </summary>
    /// <param name="text">Text to analyse, typically a title.</param>
    /// <returns>An ISO 639-1 code, or <c>null</c> when the script is inconclusive.</returns>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var hasKana = false;

        foreach (var ch in text)
        {
            foreach (var (start, end, language) in ScriptRanges)
            {
                if (ch >= start && ch <= end)
                {
                    counts[language] = counts.GetValueOrDefault(language) + 1;
                    if (language == "ja")
                    {
                        hasKana = true;
                    }

                    break;
                }
            }
        }

        if (counts.Count == 0)
        {
            return null;
        }

        // Any kana at all marks the text as Japanese, even if kanji dominate the count.
        if (hasKana && counts.GetValueOrDefault("ja") + counts.GetValueOrDefault("zh") >= MinimumMatches)
        {
            return "ja";
        }

        var best = counts.OrderByDescending(kvp => kvp.Value).First();
        return best.Value >= MinimumMatches ? best.Key : null;
    }
}
