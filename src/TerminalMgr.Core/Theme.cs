namespace TerminalMgr.Core;

/// <summary>
/// The colors and fonts of the interface, defined once (direction "Commander"). The WPF side turns them into brushes
/// (<c>ThemeResources</c>), the terminal page receives them as CSS variables (<see cref="CssVariables"/>); a test keeps the defaults in
/// terminal.css identical to these values. A light theme would be a second <see cref="Theme"/> with the same token names.
/// </summary>
public sealed record Theme(string Name, IReadOnlyDictionary<string, string> Colors)
{
    public const string UiFont = "IBM Plex Sans";
    public const string MonoFont = "JetBrains Mono";
    /// <summary>Fallbacks when a font file is missing.</summary>
    public const string UiFontFallback = "Segoe UI";
    public const string MonoFontFallback = "Cascadia Mono, Consolas";

    public static Theme Dark { get; } = new("dark", new Dictionary<string, string>
    {
        ["bg"] = "#0e0f11",            // terminal and page
        ["bar"] = "#121316",           // icon bar, tab strip, columns
        ["raised"] = "#16171b",        // raised areas
        ["overlay"] = "#1a1c20",       // palette, popups
        ["line"] = "#23262c",
        ["line-strong"] = "#353941",
        ["text"] = "#e8e6e3",
        ["term-text"] = "#d6d3cd",
        ["text-2"] = "#a9adb5",        // secondary text
        ["accent"] = "#f5b041",        // amber
        ["on-accent"] = "#1a1205",
        ["accent-tint"] = "#26221a",   // active icon button
        ["active-tint"] = "#1d1a13",   // active clipboard entry
        ["select"] = "#2b2518",        // selected palette row
        ["connected"] = "#5fd38d",
        ["recording"] = "#ff6b6b",
    });

    /// <summary>Pairs of (foreground, background) whose contrast has to reach 4.5:1.</summary>
    public static readonly (string Foreground, string Background)[] ReadablePairs =
    [
        ("text", "bg"), ("text", "bar"), ("text", "raised"), ("text", "overlay"), ("text", "select"), ("text", "active-tint"), ("text", "accent-tint"),
        ("text-2", "bg"), ("text-2", "bar"), ("text-2", "raised"), ("text-2", "overlay"), ("text-2", "select"), ("text-2", "active-tint"), ("text-2", "accent-tint"),
        ("term-text", "bg"), ("accent", "bar"), ("accent", "active-tint"), ("accent", "accent-tint"), ("on-accent", "accent"),
        ("connected", "bar"), ("recording", "raised"),
    ];

    public string this[string token] => Colors[token];

    /// <summary>The theme as CSS custom properties: "--bg: #0e0f11;" and so on, plus the fonts.</summary>
    public IReadOnlyDictionary<string, string> CssVariables()
    {
        var variables = Colors.ToDictionary(c => "--" + c.Key, c => c.Value);
        variables["--font-ui"] = $"\"{UiFont}\", \"{UiFontFallback}\", sans-serif";
        variables["--font-mono"] = $"\"{MonoFont}\", {MonoFontFallback}, monospace";
        return variables;
    }

    public static (byte R, byte G, byte B) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) throw new FormatException("Farbe muss #rrggbb sein: " + hex);
        return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        static double Channel(byte c) { double v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(string foreground, string background)
    {
        double a = Luminance(foreground), b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
