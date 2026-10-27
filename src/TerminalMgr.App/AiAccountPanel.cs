using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Flat, compact provider summaries using the application's shared theme.</summary>
public sealed class AiAccountPanel : StackPanel
{
    public event Action<AiAccountSnapshot>? DetailsRequested;
    private static TextBlock Text(string text, bool muted = false, double size = 11)
    {
        var block = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Text2Brush" : "TextBrush");
        return block;
    }
    public void Render(IReadOnlyList<AiAccountSnapshot> accounts, DateTimeOffset now)
    {
        Children.Clear();
        foreach (var account in accounts)
        {
            var content = new StackPanel();
            var row = new DockPanel { LastChildFill = true };
            var details = new Button { Content = "›", Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0), FontSize = 17, ToolTip = account.Provider + " · alle Details" };
            details.SetResourceReference(StyleProperty, "FlatButton");
            System.Windows.Automation.AutomationProperties.SetName(details, account.Provider + " Details");
            details.Click += (_, _) => DetailsRequested?.Invoke(account);
            DockPanel.SetDock(details, Dock.Right); row.Children.Add(details);
            var names = new Grid(); names.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); names.ColumnDefinitions.Add(new());
            var name = Text(account.Provider, size: 13); name.FontWeight = FontWeights.SemiBold; names.Children.Add(name);
            var plan = Text(account.Plan, true); plan.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(plan, 1); names.Children.Add(plan); row.Children.Add(names);
            content.Children.Add(row);
            foreach (var quota in account.Quotas.Take(2))
            {
                var metric = new Grid { Height = 21 };
                foreach (var width in new[] { new GridLength(27), new GridLength(1, GridUnitType.Star), new GridLength(53), new GridLength(76) }) metric.ColumnDefinitions.Add(new() { Width = width });
                metric.Children.Add(Text(quota.Label, true));
                bool expired = quota.Expired(now);
                var track = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0), ToolTip = quota.Bucket };
                track.SetResourceReference(BackgroundProperty, "LineStrongBrush");
                track.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(.0001, quota.UsedPercent), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new() { Width = new GridLength(Math.Max(.0001, 100 - quota.UsedPercent), GridUnitType.Star) });
                var fill = new Border { Visibility = expired || quota.UsedPercent == 0 ? Visibility.Hidden : Visibility.Visible };
                fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); track.Children.Add(fill); Grid.SetColumn(track, 1); metric.Children.Add(track);
                var used = Text(expired ? "—" : $"{quota.UsedPercent:0.#}% verb."); Grid.SetColumn(used, 2); metric.Children.Add(used);
                var reset = Text(quota.ResetText(now), true); reset.HorizontalAlignment = HorizontalAlignment.Right; reset.ToolTip = quota.ResetsAt?.ToLocalTime().ToString("g"); Grid.SetColumn(reset, 3); metric.Children.Add(reset);
                content.Children.Add(metric);
            }
            string? state = account.Error ?? (account.IsStale(now) ? "Veraltet · " + account.ObservedAt?.ToLocalTime().ToString("g") : account.Quotas.Count == 0 ? account.Status : account.Quotas.Count > 2 ? $"+{account.Quotas.Count - 2} weitere Limits in Details" : null);
            if (state is not null) { var status = Text(state, true); status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 2, 0, 0); content.Children.Add(status); }
            var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 6, 12, 8), Child = content };
            border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); Children.Add(border);
        }
    }
}
