namespace TerminalMgr.Core;

public sealed record ShortcutAssignment(string Action, string Label, Shortcut Shortcut);

/// <summary>Which shortcuts the app takes for itself, and whether a configuration is free of conflicts.</summary>
public static class ShortcutRules
{
    public static readonly Shortcut TmuxPrefix = new(true, false, false, "B");

    /// <summary>Modifier sets that can be combined with 1–9 to switch sessions.</summary>
    public static readonly string[] SessionModifiers = ["Alt", "Ctrl+Alt", "Alt+Shift", "Ctrl+Shift"];

    /// <summary>Modifier sets that can be combined with 0–9 to switch the tmux window of the current session. Ctrl+Alt is left out: AltGr on a German keyboard is Ctrl+Alt.</summary>
    public static readonly string[] WindowModifiers = ["Ctrl", "Alt+Shift", "Ctrl+Shift"];

    public static IReadOnlyList<ShortcutAssignment> Assignments(AppSettings settings)
    {
        var shortcuts = settings.Shortcuts.Normalized();
        return
        [
            new("dictation", "Diktat", Shortcut.Parse(settings.Dictation.Normalized().Shortcut)),
            new("copy", "Markierung kopieren", Shortcut.Parse(shortcuts.Copy)),
            new("paste", "Zwischenablage einfügen", Shortcut.Parse(shortcuts.Paste))
        ];
    }

    /// <summary>Session switching takes the chosen modifier together with the digits 1 to 9.</summary>
    public static IReadOnlyList<ShortcutAssignment> SessionShortcuts(string modifier) =>
        Enumerable.Range(1, 9).Select(n => new ShortcutAssignment("session" + n, "Session " + n, Shortcut.Parse(modifier + "+" + n))).ToList();

    /// <summary>Window digits, next, previous, back, and the favorites that have a shortcut.</summary>
    public static IReadOnlyList<ShortcutAssignment> NavigationShortcuts(AppSettings settings)
    {
        var shortcuts = settings.Shortcuts.Normalized();
        var list = new List<ShortcutAssignment>();
        for (int n = 0; n <= 9; n++) list.Add(new("window" + n, "tmux-Fenster " + n, Shortcut.Parse(shortcuts.WindowModifier + "+" + n)));
        list.Add(new("windowNext", "Nächstes tmux-Fenster", Shortcut.Parse(shortcuts.NextWindow)));
        list.Add(new("windowPrevious", "Voriges tmux-Fenster", Shortcut.Parse(shortcuts.PreviousWindow)));
        list.Add(new("windowBack", "Zurück zum letzten Fenster", Shortcut.Parse(shortcuts.Back)));
        foreach (var favorite in settings.Favorites.Where(f => f.Shortcut.Length > 0))
            if (Shortcut.TryParse(favorite.Shortcut, out var parsed)) list.Add(new("favorite:" + favorite.Id, "Favorit „" + favorite.Label + "“", parsed));
        return list;
    }

    /// <summary>The first problem found, or null. Ctrl+B stays with tmux and no combination may be used twice.</summary>
    public static string? FindConflict(AppSettings settings)
    {
        var invalid = settings.Favorites.FirstOrDefault(f => f.Shortcut.Length > 0 && !Shortcut.TryParse(f.Shortcut, out _));
        if (invalid is not null) return $"Das Tastenkürzel „{invalid.Shortcut}“ des Favoriten „{invalid.Label}“ ist ungültig.";
        var all = Assignments(settings).Concat(SessionShortcuts(settings.Shortcuts.Normalized().SessionModifier)).Concat(NavigationShortcuts(settings)).ToList();
        var reserved = all.FirstOrDefault(a => a.Shortcut == TmuxPrefix);
        if (reserved is not null) return $"{TmuxPrefix} ist das tmux-Präfix und bleibt für tmux reserviert ({reserved.Label}).";
        var duplicate = all.GroupBy(a => a.Shortcut).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is null) return null;
        var labels = duplicate.Select(a => a.Action.StartsWith("session") ? "Sessionwechsel" : a.Action.StartsWith("window") && a.Action.Length == 7 ? "tmux-Fenster 0 bis 9" : a.Label).Distinct();
        return $"{duplicate.Key} ist doppelt belegt: {string.Join(" und ", labels)}.";
    }

    private static readonly Dictionary<string, string> ReadlineKeys = new()
    {
        ["A"] = "Zeilenanfang", ["C"] = "Programm abbrechen", ["D"] = "Eingabe beenden (EOF)", ["E"] = "Zeilenende", ["K"] = "bis Zeilenende löschen",
        ["L"] = "Bildschirm löschen", ["N"] = "nächster Verlaufseintrag", ["P"] = "voriger Verlaufseintrag", ["R"] = "Verlauf durchsuchen",
        ["T"] = "Zeichen tauschen", ["U"] = "Zeile löschen", ["W"] = "Wort löschen", ["Y"] = "Einfügen aus dem Löschpuffer (yank)", ["Z"] = "Programm anhalten"
    };

    /// <summary>Hint (not a hard rule) when a shortcut would hide a key that programs in the terminal use.</summary>
    public static string? TerminalHint(Shortcut shortcut)
    {
        if (shortcut.Ctrl && !shortcut.Alt && !shortcut.Shift && shortcut.Key.Length == 1 && char.IsAsciiDigit(shortcut.Key[0]))
            return $"{shortcut} hat im Terminal eine alte Bedeutung (Ctrl+3 ist Escape, Ctrl+2 das Nullzeichen) und ist dann dort nicht mehr erreichbar.";
        if (!shortcut.Ctrl || shortcut.Alt || shortcut.Shift || shortcut.Key.Length != 1 || !char.IsAsciiLetter(shortcut.Key[0])) return null;
        return ReadlineKeys.TryGetValue(shortcut.Key, out var use)
            ? $"{shortcut} ist in bash/readline „{use}“; diese Funktion ist in TerminalMgr dann nicht mehr erreichbar."
            : $"{shortcut} wird von Programmen im Terminal genutzt und ist dann dort nicht mehr erreichbar.";
    }
}
