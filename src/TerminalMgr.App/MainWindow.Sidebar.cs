using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TerminalMgr.Core;

namespace TerminalMgr.App;

/// <summary>
/// The compact server panel: servers (one line each) at the top, the Makyx runs in the middle with all remaining space, the AI plans at
/// the bottom. The server list and the AI area fit their rows until the user drags a splitter; the heights are remembered.
/// </summary>
public partial class MainWindow
{
    /// <summary>Height of the server list as the user dragged it; 0 fits the rows.</summary>
    private double _serverListHeight;

    /// <summary>Header of the LÄUFE and KI-ABOS areas (collapse button row) and the source line of the AI area.</summary>
    private const double SectionHeader = 32, AiSourceLine = 20;
    /// <summary>What the runs keep at least while they are open.</summary>
    private const double RunsMinimum = 120;

    private void SidebarSizeChanged(object sender, SizeChangedEventArgs e) => ApplySidebarLayout();

    private void ApplySidebarLayout()
    {
        if (ServerContent is null || ServerListRow is null || AiPanelRow is null || MakyxPanelRow is null || MakyxPanelBody is null || AiPanelBody is null) return;
        double total = ServerContent.ActualHeight > 0 ? ServerContent.ActualHeight : 600;
        bool runsOpen = !Makyx.Collapsed;

        MakyxPanelBody.Visibility = runsOpen ? Visibility.Visible : Visibility.Collapsed;
        MakyxCollapseButton.Content = (runsOpen ? "⌄" : "›") + "  MAKYX";
        AiPanelBody.Visibility = _aiCollapsed ? Visibility.Collapsed : Visibility.Visible;
        AiCollapseButton.Content = (_aiCollapsed ? "›" : "⌄") + "  KI-ABOS";

        // AI at the bottom: fits its rows (or the dragged height); the servers keep at least two rows, the runs their minimum.
        double serversAtLeast = 2 * WindowLayout.ServerRowHeight;
        double runsAtLeast = runsOpen ? RunsMinimum : SectionHeader;
        double ai = SectionHeader;
        if (!_aiCollapsed)
        {
            ai = WindowLayout.ListHeight(_aiHeight, _aiAccounts.Count, WindowLayout.AiRowHeight, SectionHeader + AiSourceLine, total, serversAtLeast + runsAtLeast + 8);
            AiPanelRow.Height = new GridLength(ai);
        }
        else AiPanelRow.Height = GridLength.Auto;

        if (runsOpen)
        {
            // The runs take what is left; the server list fits its rows.
            double servers = WindowLayout.ListHeight(_serverListHeight, _profiles.Count, WindowLayout.ServerRowHeight, 2, total - ai, RunsMinimum + 8);
            ServerListRow.Height = new GridLength(servers);
            MakyxPanelRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ServerListRow.Height = new GridLength(1, GridUnitType.Star);
            MakyxPanelRow.Height = GridLength.Auto;
        }
        // A splitter only between two areas whose heights it can change.
        ShowSplitter(ServerListSplitter, ServerSplitterRow, runsOpen);
        ShowSplitter(AiSplitter, AiSplitterRow, runsOpen && !_aiCollapsed);
    }

    private static void ShowSplitter(GridSplitter splitter, RowDefinition row, bool show)
    {
        splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        row.Height = new GridLength(show ? 4 : 0);
    }

    private void ServerSplitChanged(object sender, DragCompletedEventArgs e)
    {
        _serverListHeight = Math.Round(ServerListRow.ActualHeight);
        ApplySidebarLayout(); SaveSettings();
    }

    private void AiSplitChanged(object sender, DragCompletedEventArgs e)
    {
        if (!_aiCollapsed) _aiHeight = Math.Round(AiPanelRow.ActualHeight);
        ApplySidebarLayout(); SaveSettings();
    }
}
