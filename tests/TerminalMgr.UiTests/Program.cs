using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using TerminalMgr.App;
using TerminalMgr.Core;
using TerminalMgr.Windows;

internal static class Program
{
    private static void Pump(Func<bool> until, int milliseconds = 3000)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Normal, (_, _) => {
            if (until() || DateTime.UtcNow > deadline) frame.Continue = false;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    private static void TestClipboardCompactDesign()
    {
        var main = new MainWindow();
        var list = (ListBox)main.FindName("ClipsList");
        var now = DateTimeOffset.Now;
        var rows = new[]
        {
            new ClipItem(new ClipboardEntry("docker compose logs -f", "linuxdev", now, true)),
            new ClipItem(new ClipboardEntry("/var/www/app", "webserver", now.AddMinutes(-2))),
            new ClipItem(new ClipboardEntry("systemctl status nginx", "contabo", now.AddMinutes(-5), true)),
            new ClipItem(new ClipboardEntry("Host staging\n    HostName staging.local\n    User deploy", "Windows", now.AddMinutes(-8))),
            new ClipItem(new ClipboardEntry("git status --short", "linuxdev", now.AddMinutes(-12))),
            new ClipItem(new ClipboardEntry("journalctl -u nginx -n 100", "webserver", now.AddMinutes(-16)))
        };
        list.ItemsSource = rows;
        ((TextBlock)main.FindName("ClipCount")).Text = rows.Length.ToString();
        list.SelectedIndex = 0;
        var root = (FrameworkElement)main.Content;
        root.Measure(new Size(1100, 700)); root.Arrange(new Rect(0, 0, 1100, 700)); root.UpdateLayout();
        var header = (FrameworkElement)main.FindName("ClipboardHeader");
        Check(header.ActualHeight <= 100, "Clipboard header wastes space: " + header.ActualHeight);
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        Check(first.ActualHeight <= 72, "A one-line clipboard entry is too tall: " + first.ActualHeight);
        Check(rows[0].PinnedVisibility == Visibility.Visible && rows[1].PinnedVisibility == Visibility.Collapsed, "Pinned marker");

        string? copied = null;
        bool pasted = false, pinned = false, removed = false, expanded = false;
        var menu = ClipboardMenu.Build(rows[3], (_, _) => pasted = true, t => copied = t, _ => expanded = true,
            (_, _) => pinned = true, (_, _) => removed = true);
        var actions = menu.Items.OfType<MenuItem>().ToList();
        foreach (var action in actions) action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(pasted && pinned && removed && expanded && copied == rows[3].FullText, "Clipboard menu lost an action or copied only the preview");
        var shortMenu = ClipboardMenu.Build(rows[1], (_, _) => { }, _ => { }, _ => { }, (_, _) => { }, (_, _) => { });
        Check(!shortMenu.Items.OfType<MenuItem>().ElementAt(2).IsEnabled, "No expansion for complete short text");
        if (Environment.GetEnvironmentVariable("TERMINALMGR_RENDER_DIR") is { Length: > 0 } directory)
        {
            var panel = (FrameworkElement)main.FindName("ClipboardPanel");
            ((Panel)panel.Parent).Children.Remove(panel);
            var previewWindow = new Window { Content = panel, Width = 312, Height = 700, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
            previewWindow.Show(); previewWindow.UpdateLayout(); Pump(() => false, 150);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(directory);
            using var png = File.Create(Path.Combine(directory, "clipboard-compact.png"));
            encoder.Save(png);
        }
        Console.WriteLine($"PASS Compact clipboard: header {header.ActualHeight:F0}px, one-line row {first.ActualHeight:F0}px, menu actions retain full text");
    }
    private static void TestClipboardFullText()
    {
        var shortItem = new ClipItem(new ClipboardEntry("kurz", "dev", DateTimeOffset.Now));
        string longText = string.Join('\n', Enumerable.Range(1, 40).Select(i => "Zeile " + i));
        var longItem = new ClipItem(new ClipboardEntry(longText, "dev", DateTimeOffset.Now));
        Check(!shortItem.IsTruncated && longItem.IsTruncated && longItem.FullText == longText, "Truncation flag");
        shortItem.IsExpanded = true;
        Check(!shortItem.IsExpanded && shortItem.CollapsedVisibility == Visibility.Visible, "A complete entry must not unfold");
        var changes = new List<string>();
        longItem.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");
        longItem.IsExpanded = true;
        Check(longItem.IsExpanded && longItem.ExpandedVisibility == Visibility.Visible && longItem.CollapsedVisibility == Visibility.Collapsed && changes.Contains("IsExpanded") && changes.Contains("ExpandedVisibility"), "Unfolding must notify the bindings");
        Check(new ClipItem(longItem.Entry, expanded: true).IsExpanded && !new ClipItem(shortItem.Entry, expanded: true).IsExpanded, "State handed over when the list is rebuilt");

        // Optional: render the list (collapsed and unfolded entries) to a picture for a visual check; no window and no mouse needed.
        if (Environment.GetEnvironmentVariable("TERMINALMGR_RENDER_DIR") is { Length: > 0 } renderDirectory)
        {
            var main = new MainWindow();
            var list = (ListBox)main.FindName("ClipsList");
            var oneLine = new ClipItem(new ClipboardEntry(new string('y', 400), "devbox", DateTimeOffset.Now));
            list.ItemsSource = new[] { new ClipItem(longItem.Entry, expanded: true), new ClipItem(new ClipboardEntry("kurzer Eintrag", "Windows", DateTimeOffset.Now)), oneLine };
            list.Width = 330; list.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x17, 0x1D, 0x23));
            list.Measure(new Size(330, 900)); list.Arrange(new Rect(0, 0, 330, 900)); list.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(330, 900, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(list);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(renderDirectory);
            using var png = File.Create(Path.Combine(renderDirectory, "clipboard-list.png"));
            encoder.Save(png);
        }

        // The hover popup in a real (shown) window with the pointer test hook.
        var host = new Border { Width = 200, Height = 60 };
        var window = new Window { Width = 500, Height = 300, Content = host, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100 };
        window.Show();
        window.UpdateLayout();
        Pump(() => false, 300); // a busy runner needs a moment before the first popup can open
        try
        {
            bool overTarget = true, overPopup = false;
            var hover = new HoverPreview(Dispatcher.CurrentDispatcher, () => (800, 1000), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(120), e => ReferenceEquals(e, host) ? overTarget : overPopup);
            var entered = System.Diagnostics.Stopwatch.StartNew();
            hover.Enter(host, longText);
            Check(!hover.IsOpen, "The popup must not open at once");
            Pump(() => hover.IsOpen, 10000);
            Check(entered.ElapsedMilliseconds >= 150, $"The popup opened after only {entered.ElapsedMilliseconds} ms; the delay is 200 ms");
            Check(hover.IsOpen && hover.ShownText == longText && hover.ShownText.Split('\n').Length == 40, "Popup shows the complete text with its line breaks");
            Check(hover.MaxHeight == 600 && hover.MaxWidth == 800 && hover.TextBox.IsReadOnly && hover.TextBox.FontFamily.Source.Contains("Cascadia"), $"Limits {hover.MaxWidth}x{hover.MaxHeight}");
            overTarget = false; hover.Leave(host);
            Pump(() => !hover.IsOpen, 10000);
            Check(!hover.IsOpen, "Popup closes after the pointer left");

            overTarget = true; hover.Enter(host, "x"); overTarget = false; hover.Leave(host);
            Pump(() => false, 450);
            Check(!hover.IsOpen, "Leaving before the delay must not open it");

            overTarget = true; hover.Enter(host, longText);
            Pump(() => hover.IsOpen, 10000);
            overTarget = false; overPopup = true; hover.Leave(host);
            Pump(() => false, 500);
            Check(hover.IsOpen, "The popup must stay open while the pointer is over it (to scroll and select)");
            overPopup = false;
            Pump(() => !hover.IsOpen, 10000);
            Check(!hover.IsOpen, "Popup closes once the pointer left the popup too");

            var small = new HoverPreview(Dispatcher.CurrentDispatcher, () => (100, 100), TimeSpan.FromMilliseconds(50), pointerOver: _ => true);
            small.Enter(host, "kurz"); Pump(() => small.IsOpen, 1000);
            Check(small.MaxHeight == 120 && small.MaxWidth == 240, "Small windows keep a usable minimum");
            small.Close();
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Clipboard full text: unfolding state, hover popup delay, grace time, stays open over the popup, limits");
    }

    private static void TestUpdateDialog()
    {
        var release = new UpdateRelease(new Version(0, 2, 14), "v0.2.14", "- fix: a\n- feat: b", new Uri("https://github.com/x/y/a.zip"), new Uri("https://github.com/x/y/a.zip.sha256"), "a.zip", 1000);
        foreach (bool canInstall in new[] { true, false })
        {
            var dialog = new UpdateDialog(release, new Version(0, 2, 0), canInstall, @"C:\Users\x\AppData\Local\Programs\TerminalMgr", (_, _) => Task.FromResult("staged"));
            var content = (FrameworkElement)dialog.Content;
            content.Measure(new Size(512, double.PositiveInfinity));
            Check(content.DesiredSize.Height > 150 && dialog.Title == "Update verfügbar" && dialog.Choice == UpdateChoice.Later, "Update dialog layout: " + content.DesiredSize);
        }
        var settings = new SettingsDialog(new AppSettings { Update = new() { SkippedVersion = "0.2.9" } });
        settings.Measure(new Size(620, 900));
        Check(!settings.CheckForUpdatesNow, "Settings must not request a check by themselves");
        Console.WriteLine("PASS WPF update dialog (installable and development build) and settings with update section load");
    }

    private static void TestConfigDialogs()
    {
        var current = new AppSettings { Profiles = [new Profile { Name = "dev", Destination = "devhost" }] };
        var block = new SshHostBlock(["devhost"], "Host devhost" + Environment.NewLine + "  HostName 10.0.0.1" + Environment.NewLine + "  IdentityFile ~/.ssh/dev_key");
        var export = new ExportDialog(current, [block]);
        var exportContent = (FrameworkElement)export.Content;
        exportContent.Measure(new Size(600, double.PositiveInfinity));
        Check(exportContent.DesiredSize.Height > 300 && export.Selection is null, "Export dialog layout: " + exportContent.DesiredSize);

        var content = new ConfigContent { Profiles = [new Profile { Name = "DEV", Destination = "other" }, new Profile { Name = "neu", Destination = "n" }], SshHosts = [block] };
        var info = new ConfigFileInfo(1, "0.2.0", DateTimeOffset.Now, false);
        var import = new ImportDialog(current, content, info, new HashSet<string> { "devhost" });
        ((FrameworkElement)import.Content).Measure(new Size(680, 760));
        Check(import.Plan.Profiles.Count == 2 && import.Plan.Profiles[0].Conflict && !import.Plan.Profiles[1].Conflict, "Import plan shows one conflict and one new profile");
        Check(import.Plan.Hosts.Count == 1 && import.Plan.Hosts[0].Conflict, "Existing host alias is a conflict");
        var password = new PasswordDialog("Passwort?", "Falsches Passwort");
        var passwordContent = (FrameworkElement)password.Content;
        passwordContent.Measure(new Size(440, double.PositiveInfinity));
        Check(passwordContent.DesiredSize.Height > 80, "Password dialog layout: " + passwordContent.DesiredSize);
        var profiles = new[] { new Profile { Id = "p1", Name = "devbox", Destination = "devbox" } };
        var favorites = new FavoritesDialog([new Favorite { ProfileId = "p1", Name = "Archyx", SessionName = "main", WindowName = "archyx", Shortcut = "Ctrl+Alt+A" }], profiles,
            [new NavigationPosition("p1", "$0", "main", "@4", "archyx")]);
        var favoritesContent = (FrameworkElement)favorites.Content;
        favoritesContent.Measure(new Size(860, 560));
        Check(favoritesContent.DesiredSize.Height > 150 && favorites.Result is null, "Favorites dialog layout");
        var settingsWithFavorites = new SettingsDialog(new AppSettings { Profiles = [.. profiles], Favorites = [new Favorite { ProfileId = "p1", SessionName = "main", WindowName = "archyx" }] }, [new NavigationPosition("p1", "$0", "main", "@4", "archyx")]);
        ((FrameworkElement)settingsWithFavorites.Content).Measure(new Size(620, 3000));
        var saveFavorite = new FavoriteSaveDialog(new Favorite { ProfileId = "p1", SessionName = "main", WindowName = "archyx", Name = "archyx" }, "devbox");
        ((FrameworkElement)saveFavorite.Content).Measure(new Size(480, double.PositiveInfinity));
        Check(((FrameworkElement)saveFavorite.Content).DesiredSize.Height > 150 && saveFavorite.Result is null, "Save-favorite dialog layout");
        Console.WriteLine("PASS WPF export, import (with conflicts) and password dialogs load");
    }

    // The dictation panel sits under the clipboard list, takes at most a third of the window and scrolls its text.
    private static void TestDictationPanel()
    {
        var main = new MainWindow();
        var panel = (Border)main.FindName("DictationPanel");
        var clipboard = (DockPanel)main.FindName("ClipboardPanel");
        Check(ReferenceEquals(panel.Parent, clipboard) && DockPanel.GetDock(panel) == Dock.Bottom, "The dictation panel belongs under the clipboard list");
        Check(ReferenceEquals(((FrameworkElement)main.FindName("ClipsList")).Parent, clipboard), "The clipboard list shares the column");
        foreach (double height in new[] { 540.0, 940.0 })
        {
            panel.Visibility = Visibility.Visible;
            panel.MaxHeight = DictationLayout.MaxPanelHeight(height);
            ((TextBox)main.FindName("DictationBox")).Text = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(i => "Zeile " + i + " des Diktats"));
            var root = (FrameworkElement)main.Content;
            root.Measure(new Size(1480, height));
            root.Arrange(new Rect(0, 0, 1480, height));
            root.UpdateLayout();
            Check(panel.ActualHeight <= height / 3 + 1 && panel.ActualHeight > 100, $"Panel height {panel.ActualHeight} for a window of {height}");
            var scroll = (ScrollViewer)main.FindName("DictationScroll");
            Check(scroll.ExtentHeight > scroll.ViewportHeight, "Long text must scroll inside the panel");
            scroll.ScrollToEnd();
            root.UpdateLayout();
            Check(scroll.VerticalOffset > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 1, "Scrolled to the newest text");
            var buttons = (Button)main.FindName("DictationRecordButton");
            var bottom = buttons.TranslatePoint(new Point(0, buttons.ActualHeight), root).Y;
            Check(bottom <= height, "The buttons must stay inside the window");
        }
        Check(DictationLayout.MaxPanelHeight(300) == 150 && DictationLayout.MaxPanelHeight(900) == 300, "Height rule");
        Console.WriteLine("PASS WPF dictation panel: right column under the clipboard list, at most a third of the window, text scrolls");
    }

    // The engine that lives in the terminal page: commands out, page messages in, the common events on top.
    private static void TestWebSpeechEngine()
    {
        var posted = new List<string>();
        var engine = new WebSpeechEngine(m => posted.Add(System.Text.Json.JsonSerializer.Serialize(m)), new DictationOptions { Language = "de" });
        var interim = new List<(string, string)>(); var results = new List<DictationResult>(); var errors = new List<Exception>(); var states = new List<DictationState>();
        engine.Interim += (c, t) => interim.Add((c, t)); engine.Completed += r => results.Add(r); engine.Failed += e => errors.Add(e); engine.StateChanged += s => states.Add(s);
        void Page(string json) { using var doc = System.Text.Json.JsonDocument.Parse(json); engine.OnPageMessage(doc.RootElement.Clone()); }
        engine.Start();
        Check(posted.Count == 1 && posted[0].Contains("\"cmd\":\"start\"") && posted[0].Contains("de-DE"), "Start command: " + string.Join(",", posted));
        Page("{\"event\":\"started\"}");
        Page("{\"event\":\"interim\",\"index\":0,\"text\":\"Starte den\"}");
        Page("{\"event\":\"final\",\"index\":0,\"text\":\"Starte den Server\"}");
        Page("{\"event\":\"interim\",\"index\":1,\"text\":\"und öffne\"}");
        Check(interim.SequenceEqual([("", "Starte den"), ("Starte den Server", ""), ("Starte den Server", "und öffne")]), "Interim events: " + string.Join(" | ", interim));
        engine.Stop();
        Page("{\"event\":\"end\"}");
        Check(results.Count == 1 && results[0] is { Text: "Starte den Server und öffne", Manual: true } && engine.State == DictationState.Idle, "Final text from the page results");
        Check(states.SequenceEqual([DictationState.Recording, DictationState.Processing, DictationState.Idle]), "States: " + string.Join(",", states));

        engine.Start();
        Page("{\"event\":\"error\",\"error\":\"network\"}");
        Page("{\"event\":\"end\"}");
        Check(errors.Count == 1 && errors[0] is SpeechUnavailableException && results.Count == 1 && engine.State == DictationState.Idle, "A network error is reported as unavailable and ends the round without a result");
        engine.Start();
        Page("{\"event\":\"error\",\"error\":\"no-speech\"}");
        Check(engine.State == DictationState.Recording && errors.Count == 1, "no-speech is not a failure");
        engine.Cancel();
        Page("{\"event\":\"end\"}");
        Check(results.Count == 1 && posted.Last().Contains("abort"), "Cancel aborts the page recognizer and reports nothing");
        Console.WriteLine("PASS Web Speech engine: commands, provisional/final text, stop, network error as unavailable, cancel");
    }

    // WebView2 only starts once the dispatcher is running, so the test body is queued and the loop is pumped until it is done.
    private static void RunInsideDispatcherLoop(Action test)
    {
        Exception? error = null;
        bool done = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(() => { try { test(); } catch (Exception ex) { error = ex; } done = true; });
        Pump(() => done, 180000);
        if (error is not null) throw new Exception(error.Message, error);
        if (!done) throw new Exception("The WebView2 test did not finish");
    }

    // One source for colors and fonts: the WPF brushes come from Theme.Dark, terminal.css has the same defaults, the fonts are shipped.
    private static void TestThemeSources()
    {
        var resources = new ThemeDictionary();
        foreach (var (token, hex) in Theme.Dark.Colors)
        {
            string name = ThemeDictionary.Pascal(token);
            Check(resources[name + "Brush"] is System.Windows.Media.SolidColorBrush brush && brush.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex), "Brush for " + token);
        }
        Check(ThemeDictionary.Pascal("accent-tint") == "AccentTint" && ThemeDictionary.Pascal("text-2") == "Text2", "Token names");
        string terminal = Path.Combine(AppContext.BaseDirectory, "Terminal");
        string css = File.ReadAllText(Path.Combine(terminal, "terminal.css"));
        var root = System.Text.RegularExpressions.Regex.Match(css, @":root\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        var declared = System.Text.RegularExpressions.Regex.Matches(root, @"--(?<name>[a-z0-9-]+):\s*(?<value>#[0-9a-fA-F]{6})").ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value.ToLowerInvariant());
        foreach (var (token, hex) in Theme.Dark.Colors) Check(declared.GetValueOrDefault(token) == hex, $"terminal.css --{token} is {declared.GetValueOrDefault(token)}, the theme says {hex}");
        Check(declared.Count == Theme.Dark.Colors.Count, "terminal.css declares colors that the theme does not have");
        foreach (string file in new[] { "IBMPlexSans-Regular.ttf", "IBMPlexSans-Medium.ttf", "IBMPlexSans-SemiBold.ttf", "JetBrainsMono-Regular.ttf", "JetBrainsMono-Bold.ttf", "OFL-ibmplexsans.txt", "OFL-JetBrainsMono.txt" })
            Check(File.Exists(Path.Combine(terminal, "fonts", file)), "Missing font file " + file);
        foreach (var match in System.Text.RegularExpressions.Regex.Matches(css, "url\\(\"(?<path>[^\"]+)\"\\)").Cast<System.Text.RegularExpressions.Match>())
            Check(File.Exists(Path.Combine(terminal, match.Groups["path"].Value)), "terminal.css refers to a missing file: " + match.Groups["path"].Value);
        Check(!css.Contains("http://") && !css.Contains("https://") && !css.Contains("@import"), "Fonts and styles must not come from the network");
        var families = System.Windows.Media.Fonts.GetFontFamilies(new Uri(Path.Combine(terminal, "fonts") + Path.DirectorySeparatorChar)).Select(f => f.Source).ToList();
        Check(families.Any(f => f.Contains("IBM Plex Sans")) && families.Any(f => f.Contains("JetBrains Mono")), "Shipped fonts are not found by WPF: " + string.Join(", ", families));
        Console.WriteLine("PASS Theme: one source for WPF brushes and CSS variables, terminal.css defaults equal the theme, fonts are shipped and load locally");
    }

    // The Commander layout: icon bar toggles, server panel, tabs for connected servers (and the selected one), focus mode.
    private static void TestCommanderLayout()
    {
        var main = new MainWindow();
        var list = (ListBox)main.FindName("ProfilesList");
        var tabs = (ListBox)main.FindName("SessionTabs");
        var profiles = (System.Collections.ObjectModel.ObservableCollection<ProfileItem>)list.ItemsSource;
        var devbox = new ProfileItem(new Profile { Name = "devbox", Destination = "devbox" });
        var server1 = new ProfileItem(new Profile { Name = "server1", Destination = "server1" });
        var staging = new ProfileItem(new Profile { Name = "staging", Destination = "staging" });
        profiles.Add(devbox); profiles.Add(server1); profiles.Add(staging);
        var root = (FrameworkElement)main.Content;
        root.Measure(new Size(1480, 940)); root.Arrange(new Rect(0, 0, 1480, 940)); root.UpdateLayout();

        // Names the code and the old tests rely on are still there; everything the old header and status bar offered is reachable.
        foreach (string name in new[] { "ServerButton", "ClipboardButton", "DictationButton", "SettingsButton", "ServerPanel", "ProfilesList", "SessionTabs", "TerminalView", "ClipboardPanel", "ClipsList", "DictationPanel", "ConnectButton", "DisconnectButton", "TabHint" })
            Check(main.FindName(name) is not null, "Missing element " + name);
        Check(((TextBlock)main.FindName("TabHint")).Text.Contains("K alles finden"), "Palette hint: " + ((TextBlock)main.FindName("TabHint")).Text);

        // Tabs: only connected servers and the selected one, with their Alt number.
        Check(tabs.Items.Count == 0, "No tab before anything is connected or selected: " + tabs.Items.Count);
        list.SelectedItem = devbox;
        Check(tabs.Items.Count == 1 && ((TabModel)tabs.Items[0]).Item == devbox && ((TabModel)tabs.Items[0]).Number == "1", "The selected server gets a tab");
        server1.Status = "● SSH läuft";
        staging.Status = "Verbinde …";
        Check(tabs.Items.Cast<TabModel>().Select(t => t.Item.Name).SequenceEqual(["devbox", "server1", "staging"]), "Tabs: " + string.Join(",", tabs.Items.Cast<TabModel>().Select(t => t.Item.Name)));
        Check(tabs.Items.Cast<TabModel>().Select(t => t.Number).SequenceEqual(["1", "2", "3"]), "Alt numbers follow the server list");
        Check(server1.Connection == ConnectionState.Connected && staging.Connection == ConnectionState.Connecting && devbox.Connection == ConnectionState.Disconnected, "Status dots");
        tabs.SelectedItem = tabs.Items[1];
        Check(ReferenceEquals(list.SelectedItem, server1), "A click on a tab selects the server");
        list.SelectedItem = staging;
        Check(ReferenceEquals(((TabModel)tabs.SelectedItem).Item, staging), "Selecting a server selects its tab");
        server1.Status = "Getrennt";
        Check(!tabs.Items.Cast<TabModel>().Any(t => t.Item == server1), "server1 is disconnected and not selected, so it has no tab: " + string.Join(",", tabs.Items.Cast<TabModel>().Select(t => t.Item.Name)));

        // Icon bar: the server panel opens and closes, the clipboard column too, focus mode restores what was open.
        var server = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("ServerButton");
        var clipboard = (System.Windows.Controls.Primitives.ToggleButton)main.FindName("ClipboardButton");
        var serverPanel = (FrameworkElement)main.FindName("ServerPanel");
        var clipboardPanel = (FrameworkElement)main.FindName("ClipboardPanel");
        void Click(System.Windows.Controls.Primitives.ToggleButton button) { button.IsChecked = !button.IsChecked; button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
        if (server.IsChecked == true) Click(server);
        Check(serverPanel.Visibility == Visibility.Collapsed && server.IsChecked == false, "Server panel closed");
        Click(server);
        Check(serverPanel.Visibility == Visibility.Visible && server.IsChecked == true, "Server panel opened from the bar");
        Click(clipboard);
        Check(clipboardPanel.Visibility == Visibility.Collapsed && clipboard.IsChecked == false, "Clipboard column hidden from the bar");
        Click(clipboard);
        Check(clipboardPanel.Visibility == Visibility.Visible && clipboard.IsChecked == true, "Clipboard column shown again");
        var toggleFocus = typeof(MainWindow).GetMethod("ToggleFocus", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        toggleFocus.Invoke(main, [main, new RoutedEventArgs()]);
        Check(serverPanel.Visibility == Visibility.Collapsed && clipboardPanel.Visibility == Visibility.Collapsed, "Focus mode hides both side areas");
        toggleFocus.Invoke(main, [main, new RoutedEventArgs()]);
        Check(serverPanel.Visibility == Visibility.Visible && clipboardPanel.Visibility == Visibility.Visible, "Leaving focus mode restores them");
        root.Measure(new Size(1480, 940)); root.Arrange(new Rect(0, 0, 1480, 940)); root.UpdateLayout();
        Check(((FrameworkElement)main.FindName("SessionTabs")).ActualHeight > 30 && ((FrameworkElement)main.FindName("TerminalView")).ActualHeight > 600, "Tab strip and terminal are laid out");
        Console.WriteLine("PASS Commander layout: icon bar toggles, server panel, tabs for connected servers with Alt numbers and status, focus mode restores");
    }

    // The palette in the real WebView2: it is part of the page, so it covers the terminal (a WPF element would sit behind it).
    private static void TestPaletteInWebView()
    {
        // On the GitHub runner the embedded browser hung the whole UI test run (no interactive desktop); this check runs on a developer machine.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") { Console.WriteLine("SKIP Command palette in the real WebView2: not run on the CI runner"); return; }
        var view = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x10, 0x14) };
        var window = new Window { Width = 900, Height = 600, Content = view, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 60, Top = 60 };
        window.Show();
        try
        {
            string data = Path.Combine(Path.GetTempPath(), "TerminalMgr-ui-webview-" + Guid.NewGuid().ToString("N"));
            var messages = new List<string>();
            var env = Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, data);
            Pump(() => env.IsCompleted, 20000);
            if (!env.IsCompletedSuccessfully) { Console.WriteLine("SKIP Command palette in the real WebView2: no WebView2 runtime available here"); return; }
            var ensure = view.EnsureCoreWebView2Async(env.Result);
            Pump(() => ensure.IsCompleted, 20000);
            if (!ensure.IsCompletedSuccessfully || view.CoreWebView2 is null) { Console.WriteLine("SKIP Command palette in the real WebView2: the browser did not start in this session"); return; }
            var core = view.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("terminalmgr.local", Path.Combine(AppContext.BaseDirectory, "Terminal"), Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += (_, e) => messages.Add(e.WebMessageAsJson);
            core.Navigate("https://terminalmgr.local/index.html");
            Pump(() => messages.Any(m => m.Contains("\"ready\"")), 20000);
            if (messages.Count == 0 && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") { Console.WriteLine("SKIP Command palette in the real WebView2: the page did not load on this runner"); return; }
            Check(messages.Any(m => m.Contains("\"ready\"")), "The terminal page did not report ready");
            string Eval(string script) { var task = core.ExecuteScriptAsync(script); Pump(() => task.IsCompleted, 5000); return task.IsCompletedSuccessfully ? task.Result : "timeout"; }
            core.PostWebMessageAsJson("{\"type\":\"create\",\"id\":\"one\"}");
            Pump(() => Eval("document.querySelectorAll('.session.active').length") == "1", 5000);
            core.PostWebMessageAsJson("{\"type\":\"shortcuts\",\"shortcuts\":{\"palette\":\"Ctrl+Shift+K\"},\"sessionModifier\":\"Alt\"}");
            string items = "[{\"id\":\"favorite:f1\",\"group\":\"Favoriten\",\"title\":\"Archyx\",\"subtitle\":\"devbox\",\"shortcut\":\"Ctrl+Alt+A\",\"showWhenEmpty\":true,\"searchable\":true}]";
            core.PostWebMessageAsJson("{\"type\":\"palette\",\"cmd\":\"open\",\"items\":" + items + "}");
            Pump(() => Eval("document.getElementById('palette').hidden") == "false", 5000);
            Check(Eval("document.getElementById('palette').hidden") == "false", "The palette did not open");
            // It covers the whole page, above the terminal, and the input has the keyboard focus.
            Check(Eval("(() => { const r = document.getElementById('palette').getBoundingClientRect(); return r.width >= innerWidth - 1 && r.height >= innerHeight - 1; })()") == "true", "Palette must cover the page");
            Check(Eval("document.elementFromPoint(innerWidth / 2, 120).closest('#palette') !== null") == "true", "Something is above the palette");
            Check(Eval("document.activeElement.id") == "\"palette-input\"", "Focus must be in the palette input");
            Check(Eval("document.querySelector('.palette-title').textContent") == "\"Archyx\"", "Item shown");
            string renderDirectory = Environment.GetEnvironmentVariable("TERMINALMGR_RENDER_DIR") ?? "";
            if (renderDirectory.Length > 0)
            {
                Directory.CreateDirectory(renderDirectory);
                var capture = core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, File.Create(Path.Combine(renderDirectory, "palette.png")));
                Pump(() => capture.IsCompleted, 10000);
            }
            Console.WriteLine("PASS Command palette in the real WebView2: opens over the terminal, covers the page, keyboard focus in the input");
        }
        finally { window.Close(); }
    }

    private static void TestVersionIsShown()
    {
        // Directory.Build.props is the single source: it must reach the assembly, the window title and the file version.
        string props = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Directory.Build.props"));
        // Without an override a build is Major.Minor.0; the release workflow only replaces the patch number.
        string majorMinor = System.Text.RegularExpressions.Regex.Match(props, "<MajorMinor>([^<]+)</MajorMinor>").Groups[1].Value;
        string declared = majorMinor.Length > 0 ? majorMinor + ".0" : "";
        Check(declared.Length > 0 && AppInfo.Version == declared, $"AppInfo.Version '{AppInfo.Version}' differs from Directory.Build.props '{declared}'");
        var main = new MainWindow();
        Check(main.Title == "TerminalMgr " + declared, "Window title: " + main.Title);
        var file = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(AppInfo).Assembly.Location);
        Check(file.FileVersion == declared + ".0" && file.ProductVersion!.StartsWith(declared), $"File version {file.FileVersion} / {file.ProductVersion}");
        Console.WriteLine("PASS Version from Directory.Build.props reaches the title bar, the assembly and the file version: " + declared);
    }

    private static void TestShortcutKeys()
    {
        Check(ShortcutKeys.FromWpf(System.Windows.Input.Key.Y) == "Y" && ShortcutKeys.FromWpf(System.Windows.Input.Key.D7) == "7"
            && ShortcutKeys.FromWpf(System.Windows.Input.Key.F5) == "F5" && ShortcutKeys.FromWpf(System.Windows.Input.Key.Return) == "Enter"
            && ShortcutKeys.FromWpf(System.Windows.Input.Key.Escape) == "Esc" && ShortcutKeys.FromWpf(System.Windows.Input.Key.OemComma) == ","
            && ShortcutKeys.FromWpf(System.Windows.Input.Key.LeftCtrl) is null, "WPF key names");
        // Every name the WPF side produces must be accepted by the shared parser, and match what the web page sends.
        foreach (var key in Enum.GetValues<System.Windows.Input.Key>())
            if (ShortcutKeys.FromWpf(key) is { } name)
                Check(Shortcut.TryParse("Ctrl+" + name, out var parsed) && parsed.Key == name, "Name not accepted by Shortcut: " + name);
        Console.WriteLine("PASS Shortcut key names agree between WPF and the shared parser");
    }

    private static void TestTmuxMenu()
    {
        var sessions = TmuxWindowParser.Group(TmuxWindowParser.Parse(
            "$0\t@1\t0\t0\tmy_work\tshell\tbash\n$0\t@2\t1\t1\tmy_work\teditor\tvim\n$1\t@9\t0\t1\tmain\tbash\tbash\n"));
        TmuxWindowRecord? chosen = null;
        var flat = TmuxMenu.Build(sessions, s => s.Name == "main", w => chosen = w).Cast<MenuItem>().ToList();
        Check(flat.Count == 5, "Flat menu needs 2 headers + 3 windows, got " + flat.Count);
        Check(!flat[0].IsEnabled && (string)flat[0].Header == "main · 1 Fenster · verbunden", "Header of connected session: " + flat[0].Header);
        Check(!flat[2].IsEnabled && (string)flat[2].Header == "my__work · 2 Fenster", "Header must escape underscores: " + flat[2].Header);
        Check(((string)flat[1].Header).StartsWith("● ") && ((string)flat[4].Header).StartsWith("● ") && !((string)flat[3].Header).StartsWith("●"), "Active window marker wrong");
        Check((string)flat[4].Header == "● 1: editor (vim)", "Label with running program: " + flat[4].Header);
        flat[4].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(chosen is { WindowId: "@2", SessionId: "$0" }, "Click did not choose the window by its IDs");

        var many = TmuxWindowParser.Group(Enumerable.Range(1, 30).Select(i => new TmuxWindowRecord("$0", "big", "@" + i, i, "w" + i, "bash", i == 3)));
        var nested = TmuxMenu.Build(many, _ => false, _ => { }).Cast<MenuItem>().ToList();
        Check(nested.Count == 1 && nested[0].Items.Count == 30 && nested[0].IsEnabled, "Many windows must be one submenu per session");
        Console.WriteLine("PASS tmux context menu: headers, active marker, escaping, click by IDs, submenus for many windows");
    }

    private static void SetClipboard(string text, string? marker = null, int markerValue = 1)
    {
        var data = new DataObject();
        data.SetText(text);
        if (marker is not null) data.SetData(marker, new MemoryStream(BitConverter.GetBytes(markerValue)));
        // Other programs (clipboard monitors, including an installed TerminalMgr) may hold the clipboard for a moment.
        for (int attempt = 1; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 60) { Thread.Sleep(100); }
        }
    }

    // Uses the real Windows clipboard through a message-only window; the user's text is restored afterwards.
    private static void TestClipboardMonitoring()
    {
        string? previous = null;
        try { previous = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch (System.Runtime.InteropServices.COMException) { }
        var parameters = new HwndSourceParameters("TerminalMgrClipboardTest") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 };
        using var source = new HwndSource(parameters);
        int updates = 0;
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => {
            if (msg == ClipboardNative.WmClipboardUpdate) updates++;
            return IntPtr.Zero;
        });
        if (!ClipboardNative.AddListener(source.Handle)) throw new Exception("Clipboard listener could not be registered");
        try
        {
            SetClipboard("plain text äöü\r\nzweite Zeile");
            Pump(() => updates > 0);
            if (updates == 0) throw new Exception("WM_CLIPBOARDUPDATE was not delivered");
            var plain = ClipboardNative.Snapshot(source.Handle) ?? throw new Exception("Clipboard stayed busy");
            if (plain.Text != "plain text äöü\r\nzweite Zeile" || plain.ExcludeFormatPresent || plain.CanIncludeInHistory is not null)
                throw new Exception("Plain snapshot wrong: " + plain);
            if (!ClipboardCapturePolicy.ShouldCapture(true, plain.ExcludeFormatPresent, plain.CanIncludeInHistory, false))
                throw new Exception("Plain text must be captured");

            SetClipboard("passwort", "ExcludeClipboardContentFromMonitorProcessing");
            var excluded = ClipboardNative.Snapshot(source.Handle)!;
            if (!excluded.ExcludeFormatPresent || ClipboardCapturePolicy.ShouldCapture(true, excluded.ExcludeFormatPresent, excluded.CanIncludeInHistory, false))
                throw new Exception("Exclude marker not honoured: " + excluded);

            SetClipboard("geheim", "CanIncludeInClipboardHistory", 0);
            var hidden = ClipboardNative.Snapshot(source.Handle)!;
            if (hidden.CanIncludeInHistory != 0 || ClipboardCapturePolicy.ShouldCapture(true, hidden.ExcludeFormatPresent, hidden.CanIncludeInHistory, false))
                throw new Exception("CanIncludeInClipboardHistory=0 not honoured: " + hidden);
            Console.WriteLine("PASS Real Windows clipboard: change notification, text snapshot and password-manager markers");
        }
        finally
        {
            ClipboardNative.RemoveListener(source.Handle);
            try { if (previous is not null) Clipboard.SetText(previous); else Clipboard.Clear(); } catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // These tests open real MainWindow instances, which load and save settings.json. Never in the user's own data folder.
            Environment.SetEnvironmentVariable("TERMINALMGR_DATA_DIR", Path.Combine(Path.GetTempPath(), "TerminalMgr-uitest-" + Guid.NewGuid().ToString("N")));

            var app = new App();
            app.InitializeComponent();
            if (args.Contains("--clipboard-only")) { TestClipboardCompactDesign(); TestClipboardFullText(); return 0; }
            var main = new MainWindow();
            var list = (ListBox)main.FindName("ProfilesList");
            ((IList)list.ItemsSource).Add(new ProfileItem(new Profile { Name = "Testserver", Destination = "test" }));
            main.Measure(new Size(1480, 940));
            main.Arrange(new Rect(0, 0, 1480, 940));
            main.UpdateLayout();
            if (main.Icon is null) throw new Exception("Window icon is missing");
            var profile = new ProfileDialog(new Profile { Name = "Test", Destination = "test" });
            profile.Measure(new Size(560, 730));
            var sessions = new TmuxSessionDialog(new Profile { Name = "Test", Destination = "test" });
            sessions.Measure(new Size(580, 430));
            var settings = new SettingsDialog(new AppSettings());
            settings.Measure(new Size(560, 420));
            var clips = (ListBox)main.FindName("ClipsList");
            clips.ItemsSource = new[] { new ClipItem(new ClipboardEntry("eins\nzwei\ndrei", "dev", DateTimeOffset.Now)) };
            main.UpdateLayout();
            Console.WriteLine("PASS WPF main window, icon, profile, tmux and settings dialog, clipboard item template load");
            TestClipboardCompactDesign();
            TestClipboardFullText();
            TestUpdateDialog();
            TestConfigDialogs();
            TestDictationPanel();
            TestThemeSources();
            TestCommanderLayout();
            TestWebSpeechEngine();
            RunInsideDispatcherLoop(TestPaletteInWebView);
            TestVersionIsShown();
            TestShortcutKeys();
            TestTmuxMenu();
            TestClipboardMonitoring();
            // Do not show or run the application: this test must never connect to SSH.
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
