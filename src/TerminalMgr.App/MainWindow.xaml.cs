using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProfileItem> _profiles = [];
    private readonly List<ClipboardEntry> _clips = [];
    private readonly Dictionary<string, PtySession> _sessions = [];
    private readonly HashSet<string> _connecting = [];
    private readonly HashSet<string> _terminals = [];
    private readonly Dictionary<string, TaskCompletionSource<(int Cols, int Rows)>> _created = [];
    private readonly Dictionary<string, (int Cols, int Rows)> _sizes = [];
    private readonly Channel<(string Id, string Data)> _output = Channel.CreateBounded<(string, string)>(128);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _flush;
    private readonly SettingsStore _store;
    private readonly string _dataDirectory;
    private bool _ready, _closing, _closed, _canSave = true;
    private int _fontSize = 14;
    private double _serverWidth = 220, _clipWidth = 300;

    public MainWindow()
    {
        InitializeComponent();
        _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerminalMgr");
        _store = new SettingsStore(_dataDirectory);
        ProfilesList.ItemsSource = _profiles;
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => FlushOutput(), Dispatcher);
        _flush.Start();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _store.Load();
            _fontSize = Math.Clamp(settings.FontSize, 10, 28);
            foreach (var profile in settings.Profiles) _profiles.Add(new(profile));
            _clips.AddRange(settings.PinnedClips.Where(c => c.Pinned));
            if (_profiles.Count == 0)
            {
                var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
                if (File.Exists(config)) AddImported(config);
            }
            RenderClips();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _canSave = false;
            ShowError("Einstellungen konnten nicht geladen werden. Die vorhandene Datei wird nicht überschrieben.", ex);
        }
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDirectory, "WebView2"));
            await TerminalView.EnsureCoreWebView2Async(environment);
            var core = TerminalView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("terminalmgr.local", Path.Combine(AppContext.BaseDirectory, "Terminal"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => {
                if (args.Uri != "https://terminalmgr.local/index.html") args.Cancel = true;
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.WebMessageReceived += WebMessageReceived;
            core.ProcessFailed += (_, _) => StatusText.Text = "Terminalanzeige ausgefallen. App bitte neu starten.";
            core.Navigate("https://terminalmgr.local/index.html");
        }
        catch (Exception ex)
        {
            LoadingText.Text = "Terminal konnte nicht gestartet werden. Bitte Microsoft Edge WebView2 Runtime installieren bzw. reparieren. " + ex.Message;
        }
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closing || e.Source != "https://terminalmgr.local/index.html") return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var m = document.RootElement;
            string type = m.GetProperty("type").GetString() ?? "";
            string id = m.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? "" : "";
            switch (type)
            {
                case "ready":
                    _ready = true;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    Post(new { type = "font", size = _fontSize });
                    if (_profiles.Count > 0) ProfilesList.SelectedIndex = 0;
                    await ConnectAutomaticAsync();
                    break;
                case "created":
                    var size = (m.GetProperty("cols").GetInt32(), m.GetProperty("rows").GetInt32());
                    _sizes[id] = size;
                    if (_created.Remove(id, out var completion)) completion.TrySetResult(size);
                    break;
                case "resize":
                    int cols = Math.Clamp(m.GetProperty("cols").GetInt32(), 2, 1000);
                    int rows = Math.Clamp(m.GetProperty("rows").GetInt32(), 2, 1000);
                    _sizes[id] = (cols, rows);
                    if (_sessions.TryGetValue(id, out var resized)) await Task.Run(() => resized.Resize(cols, rows));
                    if (Selected?.Profile.Id == id) UpdateDimensions(id);
                    break;
                case "input":
                    if (_sessions.TryGetValue(id, out var input))
                        await input.SendAsync(m.GetProperty("data").GetString() ?? "");
                    break;
                case "switch":
                    int index = m.GetProperty("index").GetInt32();
                    if (index >= 0 && index < _profiles.Count) ProfilesList.SelectedIndex = index;
                    break;
                case "copy":
                    string text = m.GetProperty("text").GetString() ?? "";
                    if (text.Length > 0)
                    {
                        AddClip(text, _profiles.FirstOrDefault(p => p.Profile.Id == id)?.Name ?? "Terminal");
                        try { Clipboard.SetText(text); }
                        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "Eintrag gesammelt; Windows-Zwischenablage ist belegt."; }
                    }
                    break;
                case "pasteRequest":
                    PasteSystemClipboard(id);
                    break;
            }
        }
        catch (Exception ex) { ShowError("Terminalaktion fehlgeschlagen.", ex); }
    }

    private ProfileItem? Selected => ProfilesList.SelectedItem as ProfileItem;
    private void Post(object message)
    {
        if (_ready && !_closed) TerminalView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task ConnectAsync(ProfileItem item, string? tmuxSessionId = null)
    {
        string id = item.Profile.Id;
        if (!_ready) { StatusText.Text = "Das Terminal wird noch vorbereitet."; return; }
        if (_closing || _sessions.ContainsKey(id) || !_connecting.Add(id)) return;
        PtySession? pty = null;
        try
        {
            item.Profile.Validate();
            ProfilesList.SelectedItem = item;
            if (_terminals.Remove(id)) Post(new { type = "remove", id });
            var completion = new TaskCompletionSource<(int Cols, int Rows)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _created[id] = completion;
            Post(new { type = "create", id });
            var size = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), _shutdown.Token);
            _terminals.Add(id);
            string ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");
            if (!File.Exists(ssh)) throw new FileNotFoundException("Windows OpenSSH-Client ist nicht installiert.", ssh);
            pty = new PtySession();
            var owned = pty;
            pty.Output += data => {
                try { _output.Writer.WriteAsync((id, data), _shutdown.Token).AsTask().GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            };
            pty.Error += error => Dispatcher.BeginInvoke(() => StatusText.Text = error);
            pty.Exited += code => Dispatcher.BeginInvoke(async () => {
                if (_sessions.TryGetValue(id, out var current) && ReferenceEquals(current, owned))
                {
                    _sessions.Remove(id);
                    item.Status = "Beendet · Exit " + code;
                    Post(new { type = "output", id, data = "\r\n\x1b[90m[SSH beendet, Exit " + code + "]\x1b[0m\r\n" });
                    await owned.DisposeAsync();
                }
            });
            _sessions[id] = pty;
            pty.Start(ssh, tmuxSessionId is null ? SshCommand.Arguments(item.Profile) : SshCommand.TmuxAttachArguments(item.Profile, tmuxSessionId), size.Cols, size.Rows);
            item.Status = item.Profile.AutoConnect ? "● SSH läuft · Autostart" : "● SSH läuft";
            StatusText.Text = "SSH läuft. Anmeldung und Host-Key-Abfragen erscheinen im Terminal.";
            Post(new { type = "focus", id });
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex)
        {
            _sessions.Remove(id);
            item.Status = "Verbindung fehlgeschlagen";
            ShowError("Verbindung zu " + item.Name + " fehlgeschlagen.", ex);
            if (pty is not null) await pty.DisposeAsync();
        }
        finally { _connecting.Remove(id); _created.Remove(id); }
    }

    private async Task DisconnectAsync(ProfileItem item)
    {
        if (_sessions.Remove(item.Profile.Id, out var session))
        {
            await session.DisposeAsync();
            item.Status = item.Profile.AutoConnect ? "Autostart · getrennt" : "Getrennt";
            Post(new { type = "output", id = item.Profile.Id, data = "\r\n\x1b[90m[Verbindung getrennt]\x1b[0m\r\n" });
        }
    }

    private void FlushOutput()
    {
        // Bounded queue applies backpressure to chatty remote processes.
        int characters = 0;
        while (characters < 65536 && _output.Reader.TryRead(out var chunk))
        {
            if (!_closing) Post(new { type = "output", id = chunk.Id, data = chunk.Data });
            characters += chunk.Data.Length;
        }
    }

    private async Task ConnectAutomaticAsync()
    {
        // Sequential UI creation keeps each terminal's initial size deterministic.
        var selected = Selected;
        foreach (var item in _profiles.Where(p => p.Profile.AutoConnect).ToArray()) await ConnectAsync(item);
        if (selected is not null) ProfilesList.SelectedItem = selected;
    }
    private async void ConnectAutomatic(object sender, RoutedEventArgs e) => await ConnectAutomaticAsync();
    private async void ConnectSelected(object sender, RoutedEventArgs e) { if (Selected is { } item) await ConnectAsync(item); }
    private async void DisconnectSelected(object sender, RoutedEventArgs e) { if (Selected is { } item) await DisconnectAsync(item); }
    private async void ProfileDoubleClick(object sender, MouseButtonEventArgs e) { if (Selected is { } item) await ConnectAsync(item); }

    private void ProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } item) return;
        SessionTitle.Text = item.Name + " · " + item.Profile.Destination;
        Post(new { type = "select", id = item.Profile.Id });
        UpdateDimensions(item.Profile.Id);
    }
    private void UpdateDimensions(string id)
    {
        DimensionsText.Text = _sizes.TryGetValue(id, out var size)
            ? $"{size.Cols} Spalten × {size.Rows} Zeilen   ·   Ctrl+B → tmux   ·   {_fontSize} px"
            : $"Ctrl+B → tmux   ·   {_fontSize} px";
    }

    private async void ChooseTmuxSession(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ProfileItem item }) return;
        ProfilesList.SelectedItem = item;
        var dialog = new TmuxSessionDialog(item.Profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedSession is not { } session) return;
        await DisconnectAsync(item);
        await ConnectAsync(item, session.Id);
        SessionTitle.Text = item.Name + " · tmux: " + session.Name;
    }
    private async void OpenTmuxWindowPicker(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ProfileItem item }) return;
        ProfilesList.SelectedItem = item;
        if (_sessions.TryGetValue(item.Profile.Id, out var session))
        {
            await session.SendAsync("\x02w");
            Post(new { type = "focus", id = item.Profile.Id });
        }
        else StatusText.Text = "Zuerst verbinden. Der Aufruf setzt eine laufende tmux-Session mit Präfix Ctrl+B voraus.";
    }
    private async void ContextConnect(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ProfileItem item }) await ConnectAsync(item);
    }
    private async void ContextDisconnect(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ProfileItem item }) await DisconnectAsync(item);
    }
    private void ContextEdit(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ProfileItem item }) { ProfilesList.SelectedItem = item; EditProfile(sender, e); }
    }
    private void DragClip(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && sender is TextBlock { Tag: ClipItem item } block)
            DragDrop.DoDragDrop(block, new DataObject(DataFormats.UnicodeText, item.Entry.Text), DragDropEffects.Copy);
    }
    private void AddProfile(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog(null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } profile)
        {
            var item = new ProfileItem(profile);
            _profiles.Add(item); ProfilesList.SelectedItem = item; SaveSettings();
        }
    }

    private async void EditProfile(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        var dialog = new ProfileDialog(item.Profile) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.DeleteRequested)
        {
            await DisconnectAsync(item);
            Post(new { type = "remove", id = item.Profile.Id });
            _terminals.Remove(item.Profile.Id);
            _profiles.Remove(item);
        }
        else if (dialog.Result is { } profile)
        {
            item.Profile = profile; item.Refresh();
            SessionTitle.Text = item.Name + " · " + profile.Destination;
            StatusText.Text = "Profil gespeichert. Änderungen gelten ab der nächsten Verbindung.";
        }
        SaveSettings();
    }

    private void ImportProfiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "OpenSSH-Config importieren", Filter = "Alle Dateien|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
        if (dialog.ShowDialog(this) != true) return;
        try { AddImported(dialog.FileName); SaveSettings(); }
        catch (Exception ex) { ShowError("SSH-Config konnte nicht importiert werden.", ex); }
    }
    private void AddImported(string path)
    {
        int count = 0;
        foreach (var profile in SshConfigImporter.Import(path))
        {
            if (_profiles.Any(p => string.Equals(p.Profile.Destination, profile.Destination, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Profile.ConfigPath, profile.ConfigPath, StringComparison.OrdinalIgnoreCase))) continue;
            _profiles.Add(new(profile)); count++;
        }
        StatusText.Text = count + " Profile importiert. Automatische Verbindung im Profil aktivieren.";
    }

    private void AddClip(string text, string source)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > 1_000_000) { StatusText.Text = "Clipboard-Eintrag ist größer als 1 MB."; return; }
        if (_clips.Any(c => c.Text == text && c.Pinned)) return;
        _clips.RemoveAll(c => c.Text == text && !c.Pinned);
        _clips.Insert(0, new(text, source, DateTimeOffset.Now));
        while (_clips.Count(c => !c.Pinned) > 100)
            _clips.RemoveAt(_clips.FindLastIndex(c => !c.Pinned));
        RenderClips();
    }
    private void RenderClips()
    {
        if (ClipsList is null) return;
        string search = ClipSearch?.Text ?? "";
        ClipsList.ItemsSource = _clips.Where(c => (PinnedOnly?.IsChecked != true || c.Pinned)
            && (c.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Source.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new ClipItem(c)).ToList();
    }
    private void FilterClips(object sender, RoutedEventArgs e) => RenderClips();
    private void ImportClipboard(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) AddClip(Clipboard.GetText(), "Windows"); }
        catch (Exception ex) { ShowError("Zwischenablage konnte nicht gelesen werden.", ex); }
    }
    private void PasteSystemClipboard(string id)
    {
        try { if (_sessions.ContainsKey(id) && Clipboard.ContainsText()) Post(new { type = "paste", id, text = Clipboard.GetText() }); }
        catch (Exception ex) { ShowError("Einfügen fehlgeschlagen.", ex); }
    }
    private void PasteClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipItem clip } && Selected is { } item && _sessions.ContainsKey(item.Profile.Id))
            Post(new { type = "paste", id = item.Profile.Id, text = clip.Entry.Text });
        else StatusText.Text = "Zum Einfügen zuerst eine laufende Session auswählen.";
    }
    private void PinClip(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClipItem clip }) return;
        int index = _clips.IndexOf(clip.Entry);
        if (index >= 0) _clips[index] = clip.Entry with { Pinned = !clip.Entry.Pinned };
        RenderClips(); SaveSettings();
    }
    private void RemoveClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipItem clip }) { _clips.Remove(clip.Entry); RenderClips(); SaveSettings(); }
    }
    private void ClearClipboardHistory(object sender, RoutedEventArgs e) { _clips.RemoveAll(c => !c.Pinned); RenderClips(); }

    private void SaveSettings()
    {
        if (!_canSave) return;
        try { _store.Save(new() { Profiles = _profiles.Select(p => p.Profile).ToList(),
            PinnedClips = _clips.Where(c => c.Pinned).ToList(), FontSize = _fontSize }); }
        catch (Exception ex) { ShowError("Einstellungen konnten nicht gespeichert werden.", ex); }
    }
    private void FontSmaller(object sender, RoutedEventArgs e) => SetFont(_fontSize - 1);
    private void FontLarger(object sender, RoutedEventArgs e) => SetFont(_fontSize + 1);
    private void SetFont(int size) { _fontSize = Math.Clamp(size, 10, 28); Post(new { type = "font", size = _fontSize }); SaveSettings(); }
    private void ToggleClipboard(object sender, RoutedEventArgs e)
    {
        bool hide = ClipboardColumn.Width.Value > 0;
        if (hide) _clipWidth = ClipboardColumn.ActualWidth;
        ClipboardColumn.Width = new GridLength(hide ? 0 : _clipWidth);
        ClipboardSplitterColumn.Width = new GridLength(hide ? 0 : 4);
        ClipboardPanel.Visibility = ClipboardSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ToggleFocus(object sender, RoutedEventArgs e)
    {
        bool hide = ServerColumn.Width.Value > 0;
        if (hide) _serverWidth = ServerColumn.ActualWidth;
        ServerColumn.Width = new GridLength(hide ? 0 : _serverWidth);
        ServerSplitterColumn.Width = new GridLength(hide ? 0 : 4);
        ServerPanel.Visibility = ServerSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        if ((ClipboardColumn.Width.Value > 0) == hide) ToggleClipboard(sender, e);
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int index = key - Key.D1;
        if (index >= 0 && index < Math.Min(9, _profiles.Count)) { ProfilesList.SelectedIndex = index; e.Handled = true; }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        SaveSettings();
        _shutdown.Cancel();
        StatusText.Text = "SSH-Sessions werden geschlossen …";
        await Task.WhenAll(_sessions.Values.Select(s => s.DisposeAsync().AsTask()));
        _sessions.Clear();
        _flush.Stop();
        _closed = true;
        TerminalView.Dispose();
        Close();
    }
    private void ShowError(string context, Exception ex)
    {
        StatusText.Text = context + " " + ex.Message;
        MessageBox.Show(this, context + "\n\n" + ex.Message, "TerminalMgr", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
