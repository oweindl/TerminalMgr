namespace TerminalMgr.Core;

/// <summary>
/// A keyboard shortcut such as "Ctrl+Y". Key names are the same on both sides of the WebView bridge:
/// letters and digits as themselves, F1–F12, and a few named keys (Enter, Space, Tab, Esc, ...).
/// </summary>
public sealed record Shortcut(bool Ctrl, bool Alt, bool Shift, string Key)
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = "Enter", ["Return"] = "Enter", ["Space"] = "Space", [" "] = "Space", ["Tab"] = "Tab",
        ["Esc"] = "Esc", ["Escape"] = "Esc", ["Backspace"] = "Backspace", ["Delete"] = "Delete", ["Insert"] = "Insert",
        ["Home"] = "Home", ["End"] = "End", ["PageUp"] = "PageUp", ["PageDown"] = "PageDown",
        ["Up"] = "Up", ["Down"] = "Down", ["Left"] = "Left", ["Right"] = "Right",
        [","] = ",", ["."] = ".", ["-"] = "-", ["/"] = "/", [";"] = ";", ["'"] = "'", ["["] = "[", ["]"] = "]",
        ["`"] = "`", ["\\"] = "\\", ["="] = "="
    };

    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = new(false, false, false, "");
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false;
        string? key = null;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "": return false;
                case "ctrl" or "strg" or "control": if (ctrl) return false; ctrl = true; break;
                case "alt": if (alt) return false; alt = true; break;
                case "shift" or "umschalt": if (shift) return false; shift = true; break;
                default:
                    if (key is not null) return false;
                    key = Normalize(part);
                    if (key is null) return false;
                    break;
            }
        }
        if (key is null) return false;
        bool function = key.Length > 1 && key[0] == 'F' && key[1..].All(char.IsAsciiDigit);
        // A shortcut must not swallow ordinary typing: plain or Shift-only keys are refused, except F1–F12.
        if (!function && !ctrl && !alt) return false;
        shortcut = new(ctrl, alt, shift, key);
        return true;
    }

    public static Shortcut Parse(string text) =>
        TryParse(text, out var shortcut) ? shortcut : throw new ArgumentException("Ungültiges Tastenkürzel: " + text);

    /// <summary>Canonical key name, or null when the key is not supported.</summary>
    public static string? Normalize(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return key.ToUpperInvariant();
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out int n) && n is >= 1 and <= 12) return "F" + n;
        return Named.GetValueOrDefault(key);
    }

    /// <summary>Maps a DOM KeyboardEvent.code ("KeyY", "Digit1", "F5", "Enter") to the key name used here.</summary>
    public static string? FromDomCode(string code)
    {
        if (code.StartsWith("Key") && code.Length == 4) return code[3..];
        if (code.StartsWith("Digit") && code.Length == 6) return code[5..];
        if (code.StartsWith("Numpad") && code.Length == 7 && char.IsAsciiDigit(code[6])) return code[6..];
        return code switch {
            "Comma" => ",", "Period" => ".", "Minus" => "-", "Slash" => "/", "Semicolon" => ";",
            "Quote" => "'", "BracketLeft" => "[", "BracketRight" => "]", "Backquote" => "`", "Backslash" => "\\", "Equal" => "=",
            "ArrowUp" => "Up", "ArrowDown" => "Down", "ArrowLeft" => "Left", "ArrowRight" => "Right", "NumpadEnter" => "Enter",
            _ => Normalize(code)
        };
    }

    public bool Matches(bool ctrl, bool alt, bool shift, string? key) =>
        ctrl == Ctrl && alt == Alt && shift == Shift && string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        string.Concat(Ctrl ? "Ctrl+" : "", Alt ? "Alt+" : "", Shift ? "Shift+" : "", Key);
}
