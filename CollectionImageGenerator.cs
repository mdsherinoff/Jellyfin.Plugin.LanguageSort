using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace Jellyfin.Plugin.LanguageSort;

/// <summary>
/// Draws simple poster images for language collections: a colored gradient card
/// with the language name centered on it. The color is derived deterministically
/// from the name, so each language keeps a stable, distinct look.
/// </summary>
public static class CollectionImageGenerator
{
    private const int Width = 600;
    private const int Height = 900;
    private const float MaxTextWidth = 500f;

    /// <summary>
    /// Generates a PNG poster for the given collection title.
    /// </summary>
    /// <param name="title">The collection display name, e.g. "Malayalam".</param>
    /// <returns>A stream positioned at 0 containing PNG data.</returns>
    public static MemoryStream GeneratePoster(string title)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;

        var hue = StableHue(title);
        DrawBackground(canvas, hue);
        DrawTitle(canvas, title);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var stream = new MemoryStream();
        data.SaveTo(stream);
        stream.Position = 0;
        return stream;
    }

    private static void DrawBackground(SKCanvas canvas, float hue)
    {
        using var paint = new SKPaint();
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(Width, Height),
            new[]
            {
                SKColor.FromHsl(hue, 48f, 32f),
                SKColor.FromHsl(hue, 55f, 16f)
            },
            null,
            SKShaderTileMode.Clamp);
        canvas.DrawRect(0, 0, Width, Height, paint);

        // Large translucent circle for a bit of depth.
        using var accent = new SKPaint();
        accent.Color = SKColor.FromHsl(hue, 50f, 55f).WithAlpha(28);
        accent.IsAntialias = true;
        canvas.DrawCircle(Width * 0.85f, Height * 0.15f, 260f, accent);
    }

    private static void DrawTitle(SKCanvas canvas, string title)
    {
        var typeface = ResolveTypeface(title);

        using var paint = new SKPaint();
        paint.Color = new SKColor(0xF5, 0xF5, 0xF5);
        paint.IsAntialias = true;

        var (lines, fontSize) = Layout(title, typeface);
        using var font = new SKFont(typeface, fontSize);

        var lineHeight = fontSize * 1.2f;
        var totalHeight = lineHeight * lines.Count;
        var y = (Height - totalHeight) / 2f + fontSize;

        foreach (var line in lines)
        {
            canvas.DrawText(line, Width / 2f, y, SKTextAlign.Center, font, paint);
            y += lineHeight;
        }
    }

    /// <summary>
    /// Picks a typeface that can render the title's script (Malayalam, Hangul, ...).
    /// </summary>
    private static SKTypeface ResolveTypeface(string title)
    {
        var index = 0;
        while (index < title.Length && char.IsWhiteSpace(title[index]))
        {
            index++;
        }

        if (index < title.Length)
        {
            var codepoint = char.ConvertToUtf32(title, index);
            var matched = SKFontManager.Default.MatchCharacter(null, SKFontStyle.Bold, null, codepoint);
            if (matched is not null)
            {
                return matched;
            }
        }

        return SKTypeface.Default;
    }

    /// <summary>
    /// Chooses the largest font size (120 down to 36) at which the title,
    /// wrapped at word boundaries, fits the poster width.
    /// </summary>
    private static (List<string> Lines, float FontSize) Layout(string title, SKTypeface typeface)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            words = new[] { "?" };
        }

        for (var size = 120f; size > 36f; size -= 4f)
        {
            using var font = new SKFont(typeface, size);
            if (words.Max(w => font.MeasureText(w)) > MaxTextWidth)
            {
                continue;
            }

            var lines = Wrap(words, font);
            if (lines.Count * size * 1.2f <= Height * 0.7f)
            {
                return (lines, size);
            }
        }

        using var minFont = new SKFont(typeface, 36f);
        return (Wrap(words, minFont), 36f);
    }

    private static List<string> Wrap(string[] words, SKFont font)
    {
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (font.MeasureText(candidate) <= MaxTextWidth)
            {
                current = candidate;
            }
            else
            {
                if (current.Length > 0)
                {
                    lines.Add(current);
                }

                current = word;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    /// <summary>
    /// Deterministic hue (0-360) from the title, stable across runs and processes.
    /// </summary>
    private static float StableHue(string title)
    {
        var hash = 5381u;
        foreach (var ch in title.ToUpperInvariant())
        {
            hash = (hash * 33u) ^ ch;
        }

        return hash % 360u;
    }
}
