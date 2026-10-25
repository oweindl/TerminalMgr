using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TerminalMgr.Core;
using TerminalMgr.Windows;

namespace TerminalMgr.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProfileItem> _profiles = [];
    private readonly ClipboardList _clipList = new();
    private AppSettings _settings = new();
    private HwndSource? _hwndSource;
    private uint _ownClipboardSequence;
    private bool _renderingClips;
    private Point _clipPressPoint;
    private readonly TmuxSessionQuery _tmuxQuery = new();
    // What the app knows about the tmux session each connection hangs on (by ID after a pick, by name from the profile).
    private readonly Dictionary<string, (string? Id, string? Name)> _tmuxAttached = [];
    private readonly Dictionary<string, string> _tmuxLabel = [];
    private readonly Dictionary<string, ReconnectTracker> _reconnect = [];
    private readonly Dictionary<string, CancellationTokenSource> _reconnectWait = [];
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
        Title = "TerminalMgr " + AppInfo.Version;
        _dataDirectory = AppPaths.DataDirectory();
        _store = new SettingsStore(_dataDirectory);
        ProfilesList.ItemsSource = _profiles;
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => FlushOutput(), Dispatcher);
        _flush.Start();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _settings = _store.Load();
            _fontSize = Math.Clamp(settings.FontSize, 10, 28);
            foreach (var profile in settings.Profiles) _profiles.Add(new(profile));
            _clipList.MaxUnpinned = Math.Clamp(settings.ClipHistoryLimit, 1, 1000);
            _clipList.LoadPinned(settings.PinnedClips);
            ApplySettingsToUi();
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
                    PostShortcuts();
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
                    string text = ClipboardText.TrimTrailingSpaces(m.GetProperty("text").GetString() ?? "");
                    if (text.Length > 0)
                    {
                        AddClip(text, _profiles.FirstOrDefault(p => p.Profile.Id == id)?.Name ?? "Terminal");
                        SetWindowsClipboard(text);
                    }
                    break;
                case "pasteRequest":
                    PasteSystemClipboard(id);
                    break;
                case "shortcut":
                    if (m.GetProperty("action").GetString() == "dictation") ToggleDictation();
                    break;
                case "contextPaste":
                    PasteActiveClip(id);
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

    /// <param name="automatic">True for an automatic reconnect: it must not steal the selection from what the user is doing.</param>
    private async Task ConnectAsync(ProfileItem item, string? tmuxSessionId = null, string? tmuxWindowId = null, bool automatic = false)
    {
        string id = item.Profile.Id;
        if (!_ready) { StatusText.Text = "Das Terminal wird noch vorbereitet."; return; }
        if (_closing || _sessions.ContainsKey(id) || !_connecting.Add(id)) return;
        if (!automatic) CancelReconnect(id, reset: true);
        PtySession? pty = null;
        try
        {
            item.Profile.Validate();
            if (!automatic) ProfilesList.SelectedItem = item;
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
                    string? attachedId = _tmuxAttached.TryGetValue(id, out var attached) ? attached.Id : null;
                    ForgetTmux(id);
                    item.Status = "Beendet · Exit " + code;
                    Post(new { type = "output", id, data = "\r\n\x1b[90m[SSH beendet, Exit " + code + "]\x1b[0m\r\n" });
                    await owned.DisposeAsync();
                    ScheduleReconnect(item, code, attachedId);
                }
            });
            _sessions[id] = pty;
            var arguments = tmuxSessionId is null ? SshCommand.Arguments(item.Profile)
                : tmuxWindowId is null ? SshCommand.TmuxAttachArguments(item.Profile, tmuxSessionId)
                : SshCommand.TmuxAttachArguments(item.Profile, tmuxSessionId, tmuxWindowId);
            ForgetTmux(id);
            if (tmuxSessionId is not null) _tmuxAttached[id] = (tmuxSessionId, null);
            else if (item.Profile.Mode == StartMode.Tmux) _tmuxAttached[id] = (null, item.Profile.TmuxSession);
            pty.Start(ssh, arguments, size.Cols, size.Rows);
            Reconnect(id).Connected(DateTimeOffset.Now);
            item.Status = item.Profile.AutoConnect ? "● SSH läuft · Autostart" : "● SSH läuft";
            StatusText.Text = automatic ? item.Name + ": wieder verbunden." : "SSH läuft. Anmeldung und Host-Key-Abfragen erscheinen im Terminal.";
            if (!automatic || Selected == item) Post(new { type = "focus", id });
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

    private ReconnectTracker Reconnect(string id)
    {
        if (!_reconnect.TryGetValue(id, out var tracker)) _reconnect[id] = tracker = new();
        return tracker;
    }

    /// <summary>Stops a waiting reconnect. A wanted action of the user (connect, disconnect, delete) also starts the counter over.</summary>
    private void CancelReconnect(string id, bool reset)
    {
        if (_reconnectWait.Remove(id, out var wait)) wait.Cancel();
        if (reset) Reconnect(id).Reset();
    }

    /// <summary>Called when a connection ended on its own. Only a lost connection of a profile with the option is retried.</summary>
    private void ScheduleReconnect(ProfileItem item, int exitCode, string? tmuxSessionId)
    {
        string id = item.Profile.Id;
        if (_closing) return;
        var decision = Reconnect(id).Lost(item.Profile.AutoReconnect, exitCode, DateTimeOffset.Now);
        if (!decision.Retry)
        {
            if (item.Profile.AutoReconnect && ReconnectTracker.IsConnectionLost(exitCode))
            {
                item.Status = "Wiederverbindung aufgegeben";
                Post(new { type = "output", id, data = "\x1b[90m[" + decision.Reason + "]\x1b[0m\r\n" });
            }
            return;
        }
        CancelReconnect(id, reset: false);
        var wait = _reconnectWait[id] = new CancellationTokenSource();
        _ = ReconnectAfterAsync(item, decision, tmuxSessionId, wait);
    }

    private async Task ReconnectAfterAsync(ProfileItem item, ReconnectDecision decision, string? tmuxSessionId, CancellationTokenSource wait)
    {
        string id = item.Profile.Id;
        try
        {
            string attempt = $"{decision.Attempt}/{ReconnectTracker.MaxAttempts}";
            Post(new { type = "output", id, data = $"\x1b[90m[Verbindung abgebrochen. Neuer Versuch in {decision.Delay.TotalSeconds:0} s ({attempt})]\x1b[0m\r\n" });
            for (int left = (int)Math.Ceiling(decision.Delay.TotalSeconds); left > 0; left--)
            {
                item.Status = $"Verbindung verloren · neuer Versuch in {left} s ({attempt})";
                await Task.Delay(TimeSpan.FromSeconds(1), wait.Token);
            }
            if (_closing || _sessions.ContainsKey(id)) return; // the user connected by hand in the meantime
            item.Status = $"Verbinde neu … ({attempt})";
            await ConnectAsync(item, tmuxSessionId, null, automatic: true);
        }
        catch (OperationCanceledException) { }
        finally { if (_reconnectWait.TryGetValue(id, out var current) && ReferenceEquals(current, wait)) _reconnectWait.Remove(id); }
    }

    private async Task DisconnectAsync(ProfileItem item)
    {
        CancelReconnect(item.Profile.Id, reset: true);
        if (_sessions.Remove(item.Profile.Id, out var session))
        {
            ForgetTmux(item.Profile.Id);
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
        SessionTitle.Text = TitleFor(item);
        Post(new { type = "select", id = item.Profile.Id });
        UpdateDimensions(item.Profile.Id);
    }
    private string TitleFor(ProfileItem item) => _tmuxLabel.TryGetValue(item.Profile.Id, out var label)
        ? item.Name + " · " + label : item.Name + " · " + item.Profile.Destination;
    private void ForgetTmux(string id)
    {
        _tmuxAttached.Remove(id);
        if (_tmuxLabel.Remove(id) && Selected?.Profile.Id == id) SessionTitle.Text = TitleFor(Selected);
    }
    private void UpdateDimensions(string id)
    {
        DimensionsText.Text = _sizes.TryGetValue(id, out var size)
            ? $"{size.Cols} Spalten × {size.Rows} Zeilen   ·   Ctrl+B → tmux   ·   {_fontSize} px"
            : $"Ctrl+B → tmux   ·   {_fontSize} px";
    }

    private void ServerRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var container = ItemsControl.ContainerFromElement(ProfilesList, source) as ListBoxItem;
        if (container?.DataContext is not ProfileItem item) return;
        ProfilesList.SelectedItem = item;
        var menu = new ContextMenu { PlacementTarget = container };
        void Add(string label, RoutedEventHandler click)
        {
            var entry = new MenuItem { Header = label, Tag = item };
            entry.Click += click;
            menu.Items.Add(entry);
        }
        Add("tmux-Session auswählen …", ChooseTmuxSession);
        Add("tmux-Fensterwahl öffnen (Ctrl+B w)", OpenTmuxWindowPicker);
        menu.Items.Add(new Separator());
        // The menu opens at once; the window list is inserted here when the separate SSH query answers.
        var placeholder = new MenuItem { Header = "Fenster werden geladen …", IsEnabled = false };
        menu.Items.Add(placeholder);
        menu.Items.Add(new Separator());
        Add("Verbinden", ContextConnect);
        Add("Trennen", ContextDisconnect);
        Add("Profil bearbeiten …", ContextEdit);
        var closed = new CancellationTokenSource();
        menu.Closed += (_, _) => closed.Cancel();
        menu.IsOpen = true;
        e.Handled = true;
        _ = LoadTmuxWindowsIntoMenuAsync(menu, placeholder, item, closed.Token);
    }

    private async Task LoadTmuxWindowsIntoMenuAsync(ContextMenu menu, MenuItem placeholder, ProfileItem item, CancellationToken cancel)
    {
        try
        {
            var windowsTask = _tmuxQuery.ListWindowsAsync(item.Profile, TimeSpan.FromSeconds(10), cancel);
            var attachedTask = AttachedTmuxAsync(item, cancel);
            var sessions = TmuxWindowParser.Group(await windowsTask);
            var (attachedId, attachedName) = await attachedTask;
            int at = menu.Items.IndexOf(placeholder);
            if (at < 0 || !menu.IsOpen) return;
            menu.Items.RemoveAt(at);
            if (sessions.Count == 0)
            {
                menu.Items.Insert(at, new MenuItem { Header = "Keine tmux-Sessions auf dem Server", IsEnabled = false });
                return;
            }
            var entries = TmuxMenu.Build(sessions,
                s => (attachedId is not null && s.Id == attachedId) || (attachedName is not null && s.Name == attachedName),
                window => _ = SwitchToTmuxWindowAsync(item, window));
            foreach (var entry in entries) menu.Items.Insert(at++, entry);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (menu.IsOpen)
            {
                string first = ex.Message.Split('\n')[0].Trim();
                placeholder.Header = TmuxMenu.Escape("tmux-Fenster nicht verfügbar: " + (first.Length > 70 ? first[..70] + "…" : first));
            }
        }
    }

    /// <summary>The tmux session this connection hangs on, if known: tracked ID/name, else the RemoteCommand of the SSH config.</summary>
    private async Task<(string? Id, string? Name)> AttachedTmuxAsync(ProfileItem item, CancellationToken cancel)
    {
        string id = item.Profile.Id;
        if (!_sessions.ContainsKey(id)) return (null, null);
        if (_tmuxAttached.TryGetValue(id, out var known)) return known;
        string? command = item.Profile.Mode switch {
            StartMode.SshConfig => await _tmuxQuery.RemoteCommandAsync(item.Profile, cancel),
            StartMode.Command => item.Profile.Command,
            _ => null
        };
        return (null, TmuxWindowParser.SessionNameFromCommand(command));
    }

    private async Task SwitchToTmuxWindowAsync(ProfileItem item, TmuxWindowRecord window)
    {
        string id = item.Profile.Id;
        try
        {
            ProfilesList.SelectedItem = item;
            if (_sessions.TryGetValue(id, out var pty))
            {
                await TmuxClient.SwitchToWindowAsync(pty, window.SessionId, window.WindowId, _shutdown.Token);
                _tmuxAttached[id] = (window.SessionId, null);
            }
            else
            {
                await ConnectAsync(item, window.SessionId, window.WindowId);
                if (!_sessions.ContainsKey(id)) return;
            }
            _tmuxLabel[id] = $"tmux: {window.SessionName} / {window.Name}";
            SessionTitle.Text = TitleFor(item);
            Post(new { type = "focus", id });
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { ShowError("Wechsel in das tmux-Fenster fehlgeschlagen.", ex); }
    }
    private async void ChooseTmuxSession(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ProfileItem item }) return;
        ProfilesList.SelectedItem = item;
        var dialog = new TmuxSessionDialog(item.Profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedSession is not { } session) return;
        await DisconnectAsync(item);
        await ConnectAsync(item, session.Id);
        if (!_sessions.ContainsKey(item.Profile.Id)) return;
        _tmuxLabel[item.Profile.Id] = "tmux: " + session.Name;
        SessionTitle.Text = TitleFor(item);
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
    private void ClipPressed(object sender, MouseButtonEventArgs e) => _clipPressPoint = e.GetPosition(null);
    private void DragClip(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ClipItem item } source) return;
        var now = e.GetPosition(null);
        if (Math.Abs(now.X - _clipPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _clipPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(source, new DataObject(DataFormats.UnicodeText, item.Entry.Text), DragDropEffects.Copy);
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
            SessionTitle.Text = TitleFor(item);
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
        if (_clipList.Add(text, source) is null) StatusText.Text = "Clipboard-Eintrag ist größer als 1 MB.";
        RenderClips();
    }
    private void RenderClips()
    {
        if (ClipsList is null) return;
        string search = ClipSearch?.Text ?? "";
        var items = _clipList.Items.Where(c => (PinnedOnly?.IsChecked != true || c.Pinned)
            && (c.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || c.Source.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new ClipItem(c)).ToList();
        _renderingClips = true;
        try
        {
            ClipsList.ItemsSource = items;
            ClipsList.SelectedItem = items.FirstOrDefault(i => i.Entry.Text == _clipList.Active?.Text);
            if (ClipsList.SelectedItem is not null && _clipList.Items.FirstOrDefault()?.Text == _clipList.Active?.Text) ClipsList.ScrollIntoView(ClipsList.SelectedItem);
        }
        finally { _renderingClips = false; }
    }
    private void FilterClips(object sender, RoutedEventArgs e) => RenderClips();
    private void ClipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_renderingClips && ClipsList.SelectedItem is ClipItem clip) _clipList.Activate(clip.Entry);
    }
    private void ClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindAncestor<Button>(source) is not null) return;
        if (ItemsControl.ContainerFromElement(ClipsList, source) is ListBoxItem { DataContext: ClipItem clip })
        {
            _clipList.Activate(clip.Entry);
            if (Selected is { } item) PasteActiveClip(item.Profile.Id);
        }
    }
    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node)) if (node is T match) return match;
        return null;
    }
    private void PasteSystemClipboard(string id)
    {
        try { if (_sessions.ContainsKey(id) && Clipboard.ContainsText()) Post(new { type = "paste", id, text = Clipboard.GetText() }); }
        catch (Exception ex) { ShowError("Einfügen fehlgeschlagen.", ex); }
    }
    private void PasteActiveClip(string id)
    {
        if (!_sessions.ContainsKey(id)) { StatusText.Text = "Zum Einfügen zuerst eine laufende Session auswählen."; return; }
        if (_clipList.Active is not { } clip) { StatusText.Text = "Die Clipboard-Liste ist leer."; return; }
        Post(new { type = "paste", id, text = clip.Text });
    }
    private void PasteClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipItem clip } && Selected is { } item && _sessions.ContainsKey(item.Profile.Id))
        {
            _clipList.Activate(clip.Entry);
            Post(new { type = "paste", id = item.Profile.Id, text = clip.Entry.Text });
        }
        else StatusText.Text = "Zum Einfügen zuerst eine laufende Session auswählen.";
    }
    private void PinClip(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClipItem clip }) return;
        _clipList.TogglePin(clip.Entry);
        RenderClips(); SaveSettings();
    }
    private void RemoveClip(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ClipItem clip }) { _clipList.Remove(clip.Entry); RenderClips(); SaveSettings(); }
    }
    private void ClearClipboardHistory(object sender, RoutedEventArgs e) { _clipList.ClearUnpinned(); RenderClips(); }

    private void SetWindowsClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _ownClipboardSequence = ClipboardNative.SequenceNumber; // our own write must not be captured again
        }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "Eintrag gesammelt; Windows-Zwischenablage ist belegt."; }
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WindowMessageHook);
        if (!ClipboardNative.AddListener(handle)) StatusText.Text = "Zwischenablage kann nicht überwacht werden.";
    }
    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ClipboardNative.WmClipboardUpdate) Dispatcher.BeginInvoke(CaptureWindowsClipboard);
        return IntPtr.Zero;
    }
    private void CaptureWindowsClipboard()
    {
        if (_closing) return;
        bool own = ClipboardNative.SequenceNumber == _ownClipboardSequence;
        if (own) return;
        var snapshot = ClipboardNative.Snapshot(_hwndSource?.Handle ?? IntPtr.Zero);
        if (snapshot is null) { StatusText.Text = "Windows-Zwischenablage ist belegt; Änderung nicht übernommen."; return; }
        if (ClipboardCapturePolicy.ShouldCapture(!string.IsNullOrEmpty(snapshot.Text), snapshot.ExcludeFormatPresent, snapshot.CanIncludeInHistory, own))
            AddClip(snapshot.Text!, "Windows");
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        _settings = result;
        _clipList.MaxUnpinned = result.ClipHistoryLimit;
        PostShortcuts();
        ApplySettingsToUi();
        RenderClips(); SaveSettings();
    }

    /// <summary>Texts that name a shortcut, and the "start with Windows" entry, follow the settings.</summary>
    private void ApplySettingsToUi()
    {
        var shortcuts = _settings.Shortcuts.Normalized();
        DictationButton.ToolTip = "Diktat starten (" + DictationSettings.Shortcut + ")";
        SessionHint.Text = $"{shortcuts.SessionModifier}+1 … {shortcuts.SessionModifier}+9 · Session wechseln";
        string? exe = Environment.ProcessPath;
        // Only the installed/published app registers itself, never "dotnet run" or a test host.
        if (exe is null || !string.Equals(Path.GetFileName(exe), "TerminalMgr.App.exe", StringComparison.OrdinalIgnoreCase)) return;
        try { new StartupRegistration().Apply(_settings.StartWithWindows, exe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { StatusText.Text = "Der Start mit Windows konnte nicht eingerichtet werden: " + ex.Message; }
    }

    private void SaveSettings()
    {
        if (!_canSave) return;
        try
        {
            _settings = _settings with { Profiles = _profiles.Select(p => p.Profile).ToList(),
                PinnedClips = _clipList.Items.Where(c => c.Pinned).ToList(), FontSize = _fontSize,
                ClipHistoryLimit = _clipList.MaxUnpinned };
            _store.Save(_settings);
        }
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
        // The dictation shortcut also works while a WPF control has the focus (Ctrl+Y would otherwise be "redo" in a text box).
        if (ShortcutKeys.FromEvent(e) is not { } pressed) return;
        if (Shortcut.TryParse(DictationSettings.Shortcut, out var dictation) && pressed == dictation)
        {
            if (!e.IsRepeat) ToggleDictation();
            e.Handled = true;
            return;
        }
        // Session switching: the configured modifier plus 1 to 9 (the terminal page handles the same keys while it has the focus).
        if (pressed.Key.Length == 1 && pressed.Key[0] is >= '1' and <= '9' &&
            Shortcut.Parse(_settings.Shortcuts.Normalized().SessionModifier + "+" + pressed.Key) == pressed)
        {
            int index = pressed.Key[0] - '1';
            if (index < Math.Min(9, _profiles.Count)) { ProfilesList.SelectedIndex = index; e.Handled = true; }
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        SaveSettings();
        _shutdown.Cancel();
        foreach (var wait in _reconnectWait.Values) wait.Cancel();
        DisposeDictation();
        StatusText.Text = "SSH-Sessions werden geschlossen …";
        await Task.WhenAll(_sessions.Values.Select(s => s.DisposeAsync().AsTask()));
        _sessions.Clear();
        _flush.Stop();
        if (_hwndSource is not null) { ClipboardNative.RemoveListener(_hwndSource.Handle); _hwndSource.RemoveHook(WindowMessageHook); }
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
