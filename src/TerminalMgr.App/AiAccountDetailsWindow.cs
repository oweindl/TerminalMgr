using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;

public sealed class AiAccountDetailsWindow : Window
{
    public AiAccountDetailsWindow(AiAccountSnapshot account, string source)
    {
        Title = account.Provider + " · " + source; Width = 610; Height = 650; MinWidth = 420; MinHeight = 320; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = account.Provider + " · " + account.Plan, FontSize = 20, FontWeight = FontWeights.SemiBold });
        void Fact(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(190) }); row.ColumnDefinitions.Add(new());
            var key = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }; key.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush"); row.Children.Add(key);
            var text = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0) };
            text.SetResourceReference(ForegroundProperty, "TextBrush"); Grid.SetColumn(text, 1); row.Children.Add(text); panel.Children.Add(row);
        }
        Fact("Maschine", source); Fact("Status", account.Error ?? account.Status);
        Fact("Beobachtet", account.ObservedAt?.ToLocalTime().ToString("g") ?? "Noch keine Nutzungsdaten");
        foreach (var quota in account.Quotas) Fact(quota.Bucket + " · " + quota.Label,
            quota.Expired(DateTimeOffset.UtcNow) ? "Reset erreicht · Aktualisierung ausstehend" : $"{quota.UsedPercent:0.#}% verbraucht · {100 - quota.UsedPercent:0.#}% verbleibend · " + (quota.ResetsAt?.ToLocalTime().ToString("g") ?? "Reset unbekannt"));
        foreach (var fact in account.Facts) Fact(fact.Name, fact.Value);
        Fact("Fehlende Angaben", "Nicht geliefert bedeutet unbekannt, nicht 0. Kontoweite Limits werden über Maschinen hinweg nicht addiert.");
        var close = new Button { Content = "Schließen", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) }; close.Click += (_, _) => Close(); panel.Children.Add(close);
    }
}
