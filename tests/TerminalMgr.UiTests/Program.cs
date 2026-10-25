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
            catch (System.Runtime.InteropServices.COMException) when (attempt < 20) { Thread.Sleep(100); }
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
    private static int Main()
    {
        try
        {

            var app = new App();
            app.InitializeComponent();
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
            TestUpdateDialog();
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
