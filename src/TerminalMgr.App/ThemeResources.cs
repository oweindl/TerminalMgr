using System.IO;
using System.Windows;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// The WPF half of the theme: a resource dictionary that fills itself from <see cref="Theme.Dark"/> (the same tokens the terminal page gets as
/// CSS variables). Every token becomes a brush "&lt;Token&gt;Brush" (bg → BgBrush, text-2 → Text2Brush, accent-tint → AccentTintBrush) and a color
/// "&lt;Token&gt;Color"; the fonts are "UiFont" and "MonoFont". Used in App.xaml as a merged dictionary.
/// </summary>
public sealed class ThemeDictionary : ResourceDictionary
{
    public ThemeDictionary() : this(Theme.Dark) { }

    public ThemeDictionary(Theme theme)
    {
        foreach (var (token, hex) in theme.Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            string name = Pascal(token);
            this[name + "Color"] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            this[name + "Brush"] = brush;
        }
        this["UiFont"] = Font(Theme.UiFont, Theme.UiFontFallback);
        this["MonoFont"] = Font(Theme.MonoFont, Theme.MonoFontFallback.Split(',')[0].Trim());
    }

    /// <summary>"text-2" → "Text2", "accent-tint" → "AccentTint".</summary>
    public static string Pascal(string token) =>
        string.Concat(token.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    /// <summary>The shipped font (Terminal\fonts next to the program) with a system font as fallback when the file is missing.</summary>
    public static FontFamily Font(string family, string fallback)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Terminal", "fonts");
        try
        {
            if (Directory.Exists(directory))
                return new FontFamily(new Uri(directory + Path.DirectorySeparatorChar), $"./#{family}, {fallback}");
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or IOException) { }
        return new FontFamily(fallback);
    }
}
