namespace TerminalMgr.Core;

/// <summary>
/// A saved tmux window with its own shortcut. It is bound to the server (profile), the session name and the window name,
/// not to tmux IDs: those change after a tmux restart.
/// </summary>
public sealed record Favorite
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string ProfileId { get; init; } = "";
    public string SessionName { get; init; } = "";
    public string WindowName { get; init; } = "";
    /// <summary>Canonical shortcut text such as "Ctrl+Alt+A"; empty when the favorite is only reachable from the command palette.</summary>
    public string Shortcut { get; init; } = "";

    public string Label => string.IsNullOrWhiteSpace(Name) ? $"{SessionName} › {WindowName}" : Name;
}

public enum FavoriteStatus { Found, NoSession, NoWindow }

/// <param name="Window">The window to switch to when <see cref="Status"/> is Found.</param>
public sealed record FavoriteResolution(FavoriteStatus Status, TmuxWindowRecord? Window, string Message);

public static class FavoriteResolver
{
    /// <summary>Finds the favorite's window by session and window name; with several windows of that name the first (lowest index) wins.</summary>
    public static FavoriteResolution Resolve(Favorite favorite, IEnumerable<TmuxWindowRecord> windows)
    {
        var inSession = windows.Where(w => string.Equals(w.SessionName, favorite.SessionName, StringComparison.Ordinal)).OrderBy(w => w.Index).ToList();
        if (inSession.Count == 0)
            return new(FavoriteStatus.NoSession, null, $"Favorit „{favorite.Label}“: Die tmux-Session „{favorite.SessionName}“ gibt es auf dem Server nicht.");
        var match = inSession.FirstOrDefault(w => string.Equals(w.Name, favorite.WindowName, StringComparison.Ordinal));
        return match is null
            ? new(FavoriteStatus.NoWindow, null, $"Favorit „{favorite.Label}“: In der Session „{favorite.SessionName}“ gibt es kein Fenster „{favorite.WindowName}“.")
            : new(FavoriteStatus.Found, match, "");
    }

    /// <summary>A new favorite that describes a window (used for "save as favorite").</summary>
    public static Favorite For(string profileId, TmuxWindowRecord window) =>
        new() { ProfileId = profileId, SessionName = window.SessionName, WindowName = window.Name };
}

/// <summary>A place the user was at: a window of a server.</summary>
public sealed record NavigationPosition(string ProfileId, string SessionId, string SessionName, string WindowId, string WindowName)
{
    public bool SameWindow(NavigationPosition other) =>
        ProfileId == other.ProfileId && SessionId == other.SessionId && WindowId == other.WindowId;
}

/// <summary>
/// The windows the user used, most recent first (the app's own history, not tmux "last-window"). "Back" goes to the one before the
/// current window; going back again returns, so the shortcut toggles between two windows like Alt+Tab.
/// </summary>
public sealed class NavigationHistory(int capacity = 50)
{
    private readonly List<NavigationPosition> _items = [];

    public IReadOnlyList<NavigationPosition> Items => _items;
    public NavigationPosition? Current => _items.Count > 0 ? _items[0] : null;
    /// <summary>The window before the current one, or null when there is none.</summary>
    public NavigationPosition? Previous => _items.Count > 1 ? _items[1] : null;

    /// <summary>The window is now in use: it moves to the front (a window appears once).</summary>
    public void Visit(NavigationPosition position)
    {
        _items.RemoveAll(p => p.SameWindow(position));
        _items.Insert(0, position);
        if (_items.Count > capacity) _items.RemoveRange(capacity, _items.Count - capacity);
    }

    /// <summary>A window or a whole server is gone (closed window, removed profile).</summary>
    public void Forget(Func<NavigationPosition, bool> predicate) => _items.RemoveAll(p => predicate(p));
}

/// <summary>tmux commands behind the navigation shortcuts. They run in the tmux command prompt of the own client, never in the program.</summary>
public static class TmuxNavigation
{
    /// <summary>Window <paramref name="index"/> of the session the client shows (tmux index, which depends on base-index).</summary>
    public static string SelectIndex(int index) =>
        index is >= 0 and <= 9 ? "select-window -t :" + index : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>A window by its stable ID (@12), for windows beyond index 9 and for clicks in the strip.</summary>
    public static string SelectWindowId(string windowId) =>
        TmuxWindowParser.IsWindowId(windowId) ? "select-window -t " + windowId : throw new ArgumentException("Ungültige tmux-Fenster-ID.");

    /// <summary>Lines per key press when scrolling the history of the pane.</summary>
    public const int ScrollLines = 3;

    // Scrolling goes through the tmux copy mode. "copy-mode" while already in it changes nothing; the "-and-cancel" scroll commands
    // leave the copy mode by themselves once the bottom is reached, so the user never has to press q.
    /// <summary>Scrolls up <paramref name="presses"/> × <see cref="ScrollLines"/> lines; enters the copy mode if needed.</summary>
    public static string ScrollUp(int presses) => "copy-mode ; send-keys -X -N " + Lines(presses) + " scroll-up";
    public static string ScrollDown(int presses) => InMode("send-keys -X -N " + Lines(presses) + " scroll-down-and-cancel");
    public const string PageUp = "copy-mode ; send-keys -X page-up";
    public static readonly string PageDown = InMode("send-keys -X page-down-and-cancel");
    /// <summary>Jump to the end: leaves the copy mode and shows the live screen again.</summary>
    public static readonly string ScrollEnd = InMode("send-keys -X cancel");

    private static int Lines(int presses) => Math.Clamp(presses, 1, 100) * ScrollLines;
    private static string InMode(string command) => "if-shell -F \"#{pane_in_mode}\" \"" + command + "\"";

    public const string Next = "next-window";
    public const string Previous = "previous-window";

    /// <summary>Keystrokes for the own tmux client: prefix and ":" first, the command after a pause (see <see cref="TmuxWindowParser.StepDelay"/>).</summary>
    public static IReadOnlyList<string> Steps(string tmuxCommand) => [TmuxWindowParser.Prefix + ":", tmuxCommand + "\r"];
}
