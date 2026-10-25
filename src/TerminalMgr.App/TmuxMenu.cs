using System.Windows;
using System.Windows.Controls;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>Builds the tmux part of the server context menu: every session with its windows, active window marked.</summary>
public static class TmuxMenu
{
    /// <summary>"_" would become an access key in a menu header.</summary>
    public static string Escape(string text) => text.Replace("_", "__");

    public static string SessionTitle(TmuxSessionWindows session, bool connected) =>
        $"{session.Name} · {session.Windows.Count} Fenster" + (connected ? " · verbunden" : "");

    public static IReadOnlyList<Control> Build(IReadOnlyList<TmuxSessionWindows> sessions,
        Func<TmuxSessionWindows, bool> isConnected, Action<TmuxWindowRecord> choose)
    {
        var items = new List<Control>();
        bool submenus = TmuxWindowParser.UseSubmenus(sessions);
        foreach (var session in sessions)
        {
            string title = Escape(SessionTitle(session, isConnected(session)));
            var windows = session.Windows.Select(w => WindowItem(w, choose)).ToList();
            if (submenus)
            {
                var submenu = new MenuItem { Header = title, FontWeight = isConnected(session) ? FontWeights.SemiBold : FontWeights.Normal };
                foreach (var window in windows) submenu.Items.Add(window);
                items.Add(submenu);
            }
            else
            {
                items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.Bold });
                items.AddRange(windows);
            }
        }
        return items;
    }

    private static MenuItem WindowItem(TmuxWindowRecord window, Action<TmuxWindowRecord> choose)
    {
        var item = new MenuItem { Header = (window.Active ? "● " : "    ") + Escape(window.Label), Tag = window };
        item.Click += (_, _) => choose(window);
        return item;
    }
}
