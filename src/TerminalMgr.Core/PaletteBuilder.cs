namespace TerminalMgr.Core;

/// <summary>One line of the command palette. The page filters and ranks the items; this record is what the host hands over.</summary>
/// <param name="Id">What the host runs on Enter: favorite:ID, window:PROFILE|SESSION|WINDOW, clip:N or action:NAME.</param>
/// <param name="ShowWhenEmpty">Shown while the input is empty (favorites and the windows used last).</param>
/// <param name="Searchable">Found by typing. Windows used last are not: they would only repeat the window list.</param>
public sealed record PaletteItem(string Id, string Group, string Title, string Subtitle, string Shortcut, bool ShowWhenEmpty, bool Searchable);

public sealed record PaletteServer(string ProfileId, string Name, bool Connected, string? AttachedSessionId, IReadOnlyList<TmuxWindowRecord> Windows);

public sealed record PaletteAction(string Name, string Title, string Shortcut);

/// <summary>Builds the list behind Ctrl+Shift+K: favorites, windows of all connected servers, clipboard entries and actions.</summary>
public static class PaletteBuilder
{
    public const string FavoritesGroup = "Favoriten", RecentGroup = "Zuletzt benutzt", WindowsGroup = "tmux-Fenster", ClipboardGroup = "Clipboard", ActionsGroup = "Aktionen";
    /// <summary>Order of the groups in the palette.</summary>
    public static readonly string[] GroupOrder = [FavoritesGroup, RecentGroup, WindowsGroup, ClipboardGroup, ActionsGroup];
    public const int MaxClipboardEntries = 30;
    public const int MaxRecent = 8;

    public static string WindowId(string profileId, string sessionId, string windowId) => $"window:{profileId}|{sessionId}|{windowId}";

    /// <summary>Splits "window:PROFILE|SESSION|WINDOW" again; false for anything else (so a page can not make the host run something odd).</summary>
    public static bool TryParseWindowId(string id, out string profileId, out string sessionId, out string windowId)
    {
        profileId = sessionId = windowId = "";
        if (!id.StartsWith("window:")) return false;
        string[] parts = id["window:".Length..].Split('|');
        if (parts.Length != 3 || parts[0].Length == 0 || !TmuxSessionParser.IsSessionId(parts[1]) || !TmuxWindowParser.IsWindowId(parts[2])) return false;
        (profileId, sessionId, windowId) = (parts[0], parts[1], parts[2]);
        return true;
    }

    public static IReadOnlyList<PaletteItem> Build(AppSettings settings, IReadOnlyList<PaletteServer> servers, IReadOnlyList<NavigationPosition> recent,
        IReadOnlyList<string> clipboard, IReadOnlyList<PaletteAction> actions)
    {
        var items = new List<PaletteItem>();
        string ServerName(string profileId) => servers.FirstOrDefault(s => s.ProfileId == profileId)?.Name
            ?? settings.Profiles.FirstOrDefault(p => p.Id == profileId)?.Name ?? "(Server fehlt)";

        foreach (var favorite in settings.Favorites)
            items.Add(new($"favorite:{favorite.Id}", FavoritesGroup, favorite.Label, $"{ServerName(favorite.ProfileId)} › {favorite.SessionName} › {favorite.WindowName}", favorite.Shortcut, true, true));

        foreach (var position in recent.Skip(1).Take(MaxRecent)) // the first one is the window the user is in
            items.Add(new(WindowId(position.ProfileId, position.SessionId, position.WindowId), RecentGroup, $"{ServerName(position.ProfileId)} › {position.SessionName} › {position.WindowName}", "", "", true, false));

        var modifier = settings.Shortcuts.Normalized().WindowModifier;
        foreach (var server in servers.Where(s => s.Connected))
            foreach (var session in TmuxWindowParser.Group(server.Windows))
                foreach (var window in session.Windows)
                {
                    // Index 0 to 9 of the session this server shows can be reached by its window shortcut.
                    string hint = window.SessionId == server.AttachedSessionId && window.Index is >= 0 and <= 9 ? $"{modifier}+{window.Index}" : "";
                    items.Add(new(WindowId(server.ProfileId, window.SessionId, window.WindowId), WindowsGroup, $"{server.Name} › {session.Name} › {window.Label}", window.Active ? "aktives Fenster" : "", hint, false, true));
                }

        for (int i = 0; i < Math.Min(clipboard.Count, MaxClipboardEntries); i++)
        {
            string text = clipboard[i];
            string firstLine = text.Split('\n', 2)[0].Trim();
            if (firstLine.Length == 0) continue;
            items.Add(new($"clip:{i}", ClipboardGroup, firstLine.Length > 90 ? firstLine[..90] + "…" : firstLine, text.Contains('\n') ? $"{text.Count(c => c == '\n') + 1} Zeilen, Enter fügt ein" : "Enter fügt ein", "", false, true));
        }

        foreach (var action in actions) items.Add(new($"action:{action.Name}", ActionsGroup, action.Title, "", action.Shortcut, false, true));
        return items;
    }
}
