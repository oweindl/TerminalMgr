using System.Windows;
using System.Windows.Controls;

namespace TerminalMgr.App;

/// <summary>Actions are kept in a menu so clipboard rows can devote their space to text.</summary>
public static class ClipboardMenu
{
    public static ContextMenu Create() => new() { Style = (Style)Application.Current.FindResource("ClipboardContextMenu") };

    public static ContextMenu Build(ClipItem clip, RoutedEventHandler paste, Action<string> copy,
        Action<ClipItem> expand, RoutedEventHandler pin, RoutedEventHandler remove)
    {
        var menu = Create();
        void Add(string label, RoutedEventHandler handler, string? hint = null, bool enabled = true)
        {
            var entry = new MenuItem { Header = label, Tag = clip, InputGestureText = hint ?? "", IsEnabled = enabled };
            entry.Click += handler;
            menu.Items.Add(entry);
        }
        Add("In aktive Session einfügen", paste, "Doppelklick");
        Add("In Zwischenablage kopieren", (_, _) => copy(clip.FullText));
        Add(clip.IsExpanded ? "Vorschau einklappen" : "Volltext anzeigen", (_, _) => expand(clip), enabled: clip.IsTruncated);
        menu.Items.Add(new Separator());
        Add(clip.Entry.Pinned ? "Anheften aufheben" : "Anheften", pin);
        Add("Entfernen", remove);
        return menu;
    }
}
