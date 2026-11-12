using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;

public enum TmuxRestoreChoice { Later, Restore, StartFresh }

/// <summary>
/// "Auf rechner-a waren zuletzt 2 tmux-Sessions offen": restore them, start fresh (the memory is replaced at the next snapshot) or ask again later
/// (nothing happens, the file stays until the question was answered). Closing the window counts as "Später fragen".
/// </summary>
public sealed class TmuxRestoreDialog : Window
{
    public TmuxRestoreChoice Choice { get; private set; } = TmuxRestoreChoice.Later;

    /// <summary>The question as the dialog shows it.</summary>
    public static string Question(string server, IReadOnlyList<SavedTmuxSession> sessions)
    {
        int windows = sessions.Sum(s => s.Windows.Count);
        var saved = sessions.Max(s => s.Saved);
        string when = saved > DateTimeOffset.MinValue ? " (Stand " + saved.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo("de-DE")) + ")" : "";
        return $"Auf {server} waren zuletzt {sessions.Count} tmux-{(sessions.Count == 1 ? "Session" : "Sessions")} mit {windows} {(windows == 1 ? "Fenster" : "Fenstern")} offen{when}: {TmuxSnapshot.Summary(sessions)}";
    }

    public TmuxRestoreDialog(string server, IReadOnlyList<SavedTmuxSession> sessions)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = "tmux-Sitzungen wiederherstellen · " + server;
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "tmux-Sitzungen wiederherstellen?", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = Question(server, sessions), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock {
            Text = "Wiederhergestellt werden Sessions, Fensternamen, Reihenfolge und Arbeitsordner. Die Programme darin starten nicht neu: jedes Fenster beginnt mit einer Shell.",
            TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.LightSlateGray, Margin = new Thickness(0, 10, 0, 14) });
        var buttons = new WrapPanel();
        Button Make(string text, TmuxRestoreChoice choice, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button { Content = text, IsDefault = isDefault, IsCancel = isCancel };
            button.Click += (_, _) => { Choice = choice; DialogResult = choice != TmuxRestoreChoice.Later; };
            return button;
        }
        buttons.Children.Add(Make("Wiederherstellen", TmuxRestoreChoice.Restore, isDefault: true));
        buttons.Children.Add(Make("Neu anfangen", TmuxRestoreChoice.StartFresh));
        buttons.Children.Add(Make("Später fragen", TmuxRestoreChoice.Later, isCancel: true));
        panel.Children.Add(buttons);
        Content = panel;
    }
}
