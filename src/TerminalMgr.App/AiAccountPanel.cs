using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// One line per provider for the compact sidebar: name, the first two limits as label, small bar and percent, a mark when the values are
/// stale or failed, and "›" for all details. The tooltip of the line carries plan, reset times and state.
/// </summary>
public sealed class AiAccountPanel : StackPanel
{
    public event Action<AiAccountSnapshot>? DetailsRequested;

    private static TextBlock Text(string text, bool muted = false, double size = 11)
    {
        var block = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Text2Brush" : "TextBrush");
        return block;
    }

    /// <summary>The text of the line's tooltip: provider and plan, every limit with reset time, and the state when there is one.</summary>
    public static string Summary(AiAccountSnapshot account, DateTimeOffset now)
    {
        var lines = new List<string> { account.Provider + (account.Plan.Length > 0 ? " · " + account.Plan : "") };
        foreach (var quota in account.Quotas)
            lines.Add(quota.Expired(now) ? $"{quota.Label}: abgelaufen" : $"{quota.Label}: {quota.UsedPercent:0.#} % verbraucht · zurück in {quota.ResetText(now)}");
        if (State(account, now) is { } state) lines.Add(state);
        return string.Join("\n", lines);
    }

    /// <summary>Error, stale values or a status without limits; null when the values are fresh.</summary>
    public static string? State(AiAccountSnapshot account, DateTimeOffset now) =>
        account.Error ?? (account.IsStale(now) ? "Veraltet · " + account.ObservedAt?.ToLocalTime().ToString("g") : account.Quotas.Count == 0 ? account.Status : null);

    public void Render(IReadOnlyList<AiAccountSnapshot> accounts, DateTimeOffset now)
    {
        Children.Clear();
        foreach (var account in accounts)
        {
            var row = new DockPanel { Height = WindowLayout.AiRowHeight - 1, LastChildFill = true };
            var details = new Button { Content = "›", Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), FontSize = 15, ToolTip = account.Provider + " · alle Details" };
            details.SetResourceReference(StyleProperty, "FlatButton");
            System.Windows.Automation.AutomationProperties.SetName(details, account.Provider + " Details");
            details.Click += (_, _) => DetailsRequested?.Invoke(account);
            DockPanel.SetDock(details, Dock.Right); row.Children.Add(details);

            if (State(account, now) is not null)
            {
                var mark = Text("!", size: 12); mark.FontWeight = FontWeights.Bold; mark.Margin = new Thickness(4, 0, 2, 0);
                mark.Foreground = MakyxRunsPanel.DotBrush(account.Error is null ? MakyxStatus.Waiting : MakyxStatus.Failed);
                DockPanel.SetDock(mark, Dock.Right); row.Children.Add(mark);
            }
            // Limits from the right: "5h ▬ 0%  7d ▬ 45%".
            var limits = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var quota in account.Quotas.Take(2))
            {
                bool expired = quota.Expired(now);
                var label = Text(quota.Label, true, 10); label.Margin = new Thickness(6, 0, 3, 0); limits.Children.Add(label);
                var track = new Grid { Width = 16, Height = 3, VerticalAlignment = VerticalAlignment.Center };
                track.SetResourceReference(BackgroundProperty, "LineStrongBrush");
                track.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(.0001, quota.UsedPercent), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(.0001, 100 - quota.UsedPercent), GridUnitType.Star) });
                var fill = new Border { Visibility = expired || quota.UsedPercent == 0 ? Visibility.Hidden : Visibility.Visible };
                fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); track.Children.Add(fill); limits.Children.Add(track);
                var used = Text(expired ? "—" : $"{quota.UsedPercent:0}%", size: 11); used.Margin = new Thickness(3, 0, 0, 0); used.MinWidth = 24; used.TextAlignment = TextAlignment.Right;
                limits.Children.Add(used);
            }
            DockPanel.SetDock(limits, Dock.Right); row.Children.Add(limits);

            var name = Text(account.Provider, size: 12); name.FontWeight = FontWeights.SemiBold; row.Children.Add(name);

            var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 0, 8, 0), Child = row, Background = Brushes.Transparent, ToolTip = Summary(account, now) };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            System.Windows.Automation.AutomationProperties.SetName(border, Summary(account, now).Replace("\n", ", "));
            Children.Add(border);
        }
    }
}
