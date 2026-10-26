using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TerminalMgr.App;
using TerminalMgr.Core;

// Screenshot of the real main window with demo data (neutral names, invented terminal text), for docs/design.
// Usage: Screenshot.exe <output.png> [--palette]
// Works against any version of the app that has these elements: TerminalView, ProfilesList. Nothing of the user's own data is used.
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static string? _log;
    private static void Log(string text) { if (_log is not null) File.AppendAllText(_log, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine); }

    private static void Pump(Func<bool> until, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => { if (until() || DateTime.UtcNow > deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: Screenshot <output.png> [--palette]"); return 2; }
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        _log = Path.ChangeExtension(output, ".log");
        string data = Path.Combine(Path.GetTempPath(), "TerminalMgr-screenshot-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("TERMINALMGR_DATA_DIR", data);
        Directory.CreateDirectory(data);
        string longText = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"INFO  worker-{i} gestartet, Warteschlange leer"));
        new SettingsStore(data).Save(new AppSettings
        {
            Update = new UpdateOptions { CheckOnStart = false },
            Profiles =
            [
                new Profile { Id = "p-devbox", Name = "devbox", Destination = "devbox", Mode = StartMode.Tmux, TmuxSession = "main" },
                new Profile { Name = "server1", Destination = "server1", Mode = StartMode.Tmux, TmuxSession = "work" },
                new Profile { Name = "staging", Destination = "staging" },
            ],
            Favorites =
            [
                new Favorite { Name = "Archyx", ProfileId = "p-devbox", SessionName = "main", WindowName = "archyx", Shortcut = "Ctrl+Alt+A" },
                new Favorite { Name = "Build-Log", ProfileId = "p-devbox", SessionName = "main", WindowName = "build", Shortcut = "Ctrl+Alt+B" },
            ],
            PinnedClips =
            [
                new ClipboardEntry("git log --oneline -n 5", "devbox", DateTimeOffset.Now.AddMinutes(-3), true),
                new ClipboardEntry("docker compose up -d --build", "server1", DateTimeOffset.Now.AddMinutes(-18), true),
                new ClipboardEntry(longText, "devbox", DateTimeOffset.Now.AddHours(-1), true),
                new ClipboardEntry("tail -f /var/log/app/error.log", "staging", DateTimeOffset.Now.AddHours(-2), true),
            ]
        });

        var app = new App();
        app.InitializeComponent();
        var main = new MainWindow { Width = 1480, Height = 940, WindowStartupLocation = WindowStartupLocation.Manual, Left = 20, Top = 20 };
        var view = (Microsoft.Web.WebView2.Wpf.WebView2)main.FindName("TerminalView");
        var profiles = (ListBox)main.FindName("ProfilesList");
        int result = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            try { result = Run(main, view, profiles, output, args.Contains("--palette")); }
            catch (Exception ex) { Log("FAILED " + ex); }
            app.Shutdown();
        });
        app.Run(main);
        try { Directory.Delete(data, true); } catch (IOException) { }
        return result;
    }

    private static int Run(Window main, Microsoft.Web.WebView2.Wpf.WebView2 view, ListBox profiles, string output, bool palette)
    {
        string Eval(string script) { var task = view.CoreWebView2.ExecuteScriptAsync(script); Pump(() => task.IsCompleted, 5000); return task.IsCompletedSuccessfully ? task.Result : "timeout"; }
        Pump(() => view.CoreWebView2 is not null && Eval("typeof window.TerminalPalette") != "timeout" && Eval("document.getElementById('empty') !== null") == "true", 30000);
        if (view.CoreWebView2 is null) { Console.Error.WriteLine("The terminal did not start."); return 1; }
        Log("page ready");
        Pump(() => false, 1500);
        // Connected servers: the status text drives the dot of the tab and the list.
        for (int i = 0; i < 2 && i < profiles.Items.Count; i++)
            if (profiles.Items[i] is ProfileItem item) item.Status = "● SSH läuft";
        profiles.SelectedIndex = 0;
        Log("profiles selected");
        view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"create\",\"id\":\"demo\"}");
        Pump(() => Eval("document.querySelectorAll('.session.active').length") == "1", 5000);
        Log("session created");
        string rows = Eval("document.querySelector('.xterm-rows') ? String(document.querySelector('.xterm-rows').childElementCount) : '30'");
        int lines = int.TryParse(rows, out int r) ? r : 30;
        string ansi =
            "\u001b[2J\u001b[H" +
            "\u001b[32moli@devbox\u001b[0m:\u001b[34m~/projekt\u001b[0m$ git status --short\r\n" +
            " \u001b[31mM\u001b[0m src/Konfiguration.cs\r\n \u001b[31mM\u001b[0m src/Verlauf.cs\r\n\u001b[31m??\u001b[0m docs/anleitung.md\r\n" +
            "\u001b[32moli@devbox\u001b[0m:\u001b[34m~/projekt\u001b[0m$ dotnet test\r\n" +
            "  Bestanden!  Fehlgeschlagen: 0, bestanden: 117, übersprungen: 0\r\n\r\n" +
            "\u001b[32moli@devbox\u001b[0m:\u001b[34m~/projekt\u001b[0m$ tmux list-windows\r\n" +
            "0: shell* (1 panes)\r\n1: build- (1 panes)\r\n2: archyx (1 panes)\r\n" +
            "\u001b[32moli@devbox\u001b[0m:\u001b[34m~/projekt\u001b[0m$ \u001b[7m \u001b[0m" +
            $"\u001b[{lines};1H\u001b[30;42m [main] 0:shell* 1:build- 2:archyx \u001b[0m";
        view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "output", id = "demo", data = ansi }));
        if (palette)
        {
            string items = JsonSerializer.Serialize(new[]
            {
                new { id = "favorite:f1", group = "Favoriten", title = "Archyx", subtitle = "devbox › main › archyx", shortcut = "Ctrl+Alt+A", showWhenEmpty = true, searchable = true },
                new { id = "window:p|$0|@1", group = "Zuletzt benutzt", title = "devbox › main › 1: build", subtitle = "", shortcut = "", showWhenEmpty = true, searchable = false },
            });
            view.CoreWebView2.PostWebMessageAsJson("{\"type\":\"palette\",\"cmd\":\"open\",\"items\":" + items + "}");
        }
        Pump(() => false, 2500);
        main.Activate();
        Pump(() => false, 500);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Capture(main, output);
        Console.WriteLine("Screenshot: " + output);
        return 0;
    }

    private static void Capture(Window window, string path)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var rect);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr old = SelectObject(memory, bitmap);
        PrintWindow(hwnd, memory, 2); // PW_RENDERFULLCONTENT: includes the WebView2
        SelectObject(memory, old);
        var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var file = File.Create(path)) encoder.Save(file);
        DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen);
    }
}
