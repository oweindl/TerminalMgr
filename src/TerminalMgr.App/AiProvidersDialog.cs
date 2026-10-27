using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

public sealed class AiProvidersDialog : Window
{
    private readonly ComboBox _machine = new() { Margin = new Thickness(0, 6, 0, 16) };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly PasswordBox _key = new() { Margin = new Thickness(0, 6, 0, 6), MaxLength = 2048 };
    private readonly StackPanel _openRouter = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _panel = new() { Margin = new Thickness(22) };
    private Profile? SelectedProfile => (_machine.SelectedItem as ComboBoxItem)?.Tag as Profile;

    public AiProvidersDialog(IReadOnlyList<Profile> profiles, Profile? selected)
    {
        Title = "KI-Anbieter einrichten"; Width = 640; Height = 730; MinWidth = 480; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Heading("Anmeldung und Nutzungsdaten");
        Note("Jede Anmeldung gilt für die gewählte Maschine und deren Benutzer. Kontoweite Abo-Limits, Sitzungswerte und API-Budgets bleiben getrennt. Es werden keine kostenpflichtigen Modellanfragen gestartet.");
        _panel.Children.Add(_machine);
        _machine.Items.Add(new ComboBoxItem { Content = "Dieser PC · " + Environment.UserName });
        foreach (var profile in profiles) _machine.Items.Add(new ComboBoxItem { Content = profile.Name + " · " + profile.Destination, Tag = profile });
        _machine.SelectedIndex = selected is null ? 0 : Math.Max(0, profiles.ToList().FindIndex(p => p.Id == selected.Id) + 1);
        _machine.SelectionChanged += (_, _) => { _openRouter.Visibility = SelectedProfile is null ? Visibility.Visible : Visibility.Collapsed; _state.Text = "Maschine geändert. Mit ‚Status prüfen‘ neu einlesen."; };

        Heading("ChatGPT / Codex");
        Note("Nutzt die Codex-Anmeldung auf dieser Maschine. Liefert Tarif und verfügbare Codex-Limits; keine vollständige ChatGPT-Web-Nutzungsübersicht. Codex CLI muss installiert sein.");
        Button("Mit ChatGPT anmelden …", () => Login("codex", ["login", "--device-auth"]));
        Heading("Claude Code");
        Note("Claude CLI muss installiert sein. Anmeldung öffnet ein separates Fenster. Für Limits die Statuszeile einmal einrichten; Python 3.9+ wird dafür auf dieser Maschine benötigt. Eine vorhandene Statuszeile wird gesichert und weiterhin ausgeführt.");
        Button("Bei Claude anmelden …", () => Login("claude", ["auth", "login"]));
        Button("Claude-Statuszeile einrichten", async () => await ConfigureCollectorAsync(false));
        Button("Vorherige Statuszeile wiederherstellen", async () => await ConfigureCollectorAsync(true));

        var title = new TextBlock { Text = "OpenRouter · lokaler API-Zugang", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
        _openRouter.Children.Add(title);
        _openRouter.Children.Add(new TextBlock { Text = "API-Key eingeben. ‚Verbinden‘ prüft ihn und speichert ihn in der Windows-Anmeldeinformationsverwaltung. Er wird nicht auf SSH-Server kopiert und nicht exportiert. Sichtbar sind Key-Verbrauch und verfügbare Budgets.", TextWrapping = TextWrapping.Wrap });
        _openRouter.Children.Add(_key);
        var buttons = new WrapPanel();
        var connect = new Button { Content = "Verbinden" };
        connect.Click += async (_, _) => {
            string secret = _key.Password.Trim(); _key.Clear();
            if (string.IsNullOrWhiteSpace(secret)) { _state.Text = "Bitte einen OpenRouter API-Key eingeben."; return; }
            connect.IsEnabled = false;
            try
            {
                var account = await AiAccountSource.ReadOpenRouterAsync(secret, _lifetime.Token);
                if (account.ObservedAt is null) { _state.Text = account.Status; return; }
                AiCredentialStore.SaveOpenRouter(secret); _state.Text = "OpenRouter verbunden. Key geschützt in Windows gespeichert.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or ArgumentException)
            { _state.Text = "OpenRouter konnte nicht verbunden werden. API-Key und Netzwerk prüfen."; }
            finally { connect.IsEnabled = true; }
        };
        buttons.Children.Add(connect);
        var remove = new Button { Content = "Zugang entfernen" };
        remove.Click += (_, _) => { try { AiCredentialStore.DeleteOpenRouter(); _key.Clear(); _state.Text = "Lokaler OpenRouter-Zugang entfernt. Der Key beim Anbieter bleibt bestehen."; } catch (System.ComponentModel.Win32Exception) { _state.Text = "Windows konnte den Zugang nicht entfernen."; } };
        buttons.Children.Add(remove); _openRouter.Children.Add(buttons); _panel.Children.Add(_openRouter);
        _openRouter.Visibility = SelectedProfile is null ? Visibility.Visible : Visibility.Collapsed;
        Heading("Verbindung prüfen");
        Button("Status prüfen", async () => await CheckAsync());
        _panel.Children.Add(_state);
        Note("Weitere Anbieter benötigen einen passenden Datenadapter. Ein API-Key allein liefert keine ChatGPT- oder Claude-Abo-Informationen. Verlängerung und Rechnungen sind hier nicht verfügbar.");
        Button("Schließen", Close);
        Closed += (_, _) => { _lifetime.Cancel(); _key.Clear(); };
    }
    private void Heading(string text) => _panel.Children.Add(new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
    private void Note(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) }; block.SetResourceReference(ForegroundProperty, "Text2Brush"); _panel.Children.Add(block);
    }
    private void Button(string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action(); _panel.Children.Add(button);
    }
    private void Login(string executable, string[] arguments)
    {
        try
        {
            ProcessStartInfo start;
            if (SelectedProfile is { } profile)
            {
                start = new ProcessStartInfo(TmuxSessionQuery.SshExecutable) { UseShellExecute = true };
                foreach (string arg in SshCommand.Arguments(profile with { Mode = StartMode.Command, Command = string.Join(" ", new[] { executable }.Concat(arguments).Select(SshCommand.ShellQuote)) })) start.ArgumentList.Add(arg);
            }
            else
            {
                // A separate interactive console keeps authentication out of every running terminal.
                start = new ProcessStartInfo(executable) { UseShellExecute = true };
                foreach (string arg in arguments) start.ArgumentList.Add(arg);
            }
            Process.Start(start)?.Dispose();
            _state.Text = "Anmeldung im separaten Fenster abschließen, danach ‚Status prüfen‘. Bestehende Sessions laufen weiter.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { _state.Text = "Anmeldung konnte nicht starten. CLI-Installation und SSH-Profil prüfen."; }
    }
    private async Task CheckAsync()
    {
        if (!_machine.IsEnabled) return;
        _machine.IsEnabled = false;
        _state.Text = "Kontostatus wird geprüft …";
        try
        {
            var accounts = (await new AiAccountSource().ReadAsync(SelectedProfile, _lifetime.Token)).ToList();
            if (SelectedProfile is null && AiCredentialStore.ReadOpenRouter() is { } secret) accounts.Add(await AiAccountSource.ReadOpenRouterAsync(secret, _lifetime.Token));
            _state.Text = string.Join("\n", accounts.Select(a => a.Provider + ": " + a.Plan + " · " + (a.Error ?? a.Status)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        { _state.Text = "Kontostatus nicht erreichbar. Anmeldung, Netzwerk und SSH-Key prüfen."; }
        finally { _machine.IsEnabled = true; }
    }
    private async Task ConfigureCollectorAsync(bool remove)
    {
        if (!_machine.IsEnabled) return;
        _machine.IsEnabled = false;
        _state.Text = "Claude-Statuszeile wird " + (remove ? "wiederhergestellt …" : "eingerichtet …");
        try
        {
            string script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integrations", "claude-statusline.py"), _lifetime.Token);
            string bootstrap = "import sys,os,subprocess; from pathlib import Path; p=Path.home()/'.terminalmgr/ai/claude-statusline.py'; p.parent.mkdir(parents=True,exist_ok=True,mode=0o700); p.write_text(sys.stdin.read(),encoding='utf-8'); os.chmod(p,0o700); sys.exit(subprocess.call([sys.executable,str(p),'" + (remove ? "--uninstall" : "--install") + "']))";
            string result = await AiAccountSource.RunAsync(AiAccountSource.Command(SelectedProfile, SelectedProfile is null ? "python" : "python3", "-c", bootstrap), script, _lifetime.Token);
            _state.Text = AiAccountParser.Clean(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        { _state.Text = "Einrichtung fehlgeschlagen. Python 3.9+, SSH-Key und Schreibrechte für Claude-Einstellungen prüfen."; }
        finally { _machine.IsEnabled = true; }
    }
}
