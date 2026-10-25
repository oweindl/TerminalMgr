using System.Collections;
using System.Windows;
using System.Windows.Controls;
using TerminalMgr.App;
using TerminalMgr.Core;

internal static class Program
{
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
            Console.WriteLine("PASS WPF main window, icon, profile and tmux dialog load");
            // Do not show or run the application: this test must never connect to SSH.
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
