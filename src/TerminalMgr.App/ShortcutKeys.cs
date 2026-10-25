using System.Windows.Input;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Translates WPF key events into the key names shared with the terminal page (see <see cref="Shortcut"/>).</summary>
public static class ShortcutKeys
{
    public static string? FromWpf(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.F1 and <= Key.F12) return "F" + (key - Key.F1 + 1);
        return key switch {
            Key.Return => "Enter", Key.Space => "Space", Key.Tab => "Tab", Key.Escape => "Esc", Key.Back => "Backspace",
            Key.Delete => "Delete", Key.Insert => "Insert", Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemQuestion => "/", Key.OemSemicolon => ";",
            Key.OemQuotes => "'", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemTilde => "`", Key.OemBackslash => "\\", Key.OemPlus => "=",
            _ => null
        };
    }

    /// <summary>The shortcut the user just pressed, or null for modifier-only presses and unsupported keys.</summary>
    public static Shortcut? FromEvent(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        string? name = FromWpf(key);
        if (name is null) return null;
        var modifiers = Keyboard.Modifiers;
        return new(modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Alt), modifiers.HasFlag(ModifierKeys.Shift), name);
    }
}
