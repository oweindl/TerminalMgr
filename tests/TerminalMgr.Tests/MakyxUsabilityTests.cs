using TerminalMgr.Core;

/// <summary>Note 16 (Bedienung Sessions und tmux): short names, grouping by project, own tmux views per tab, the new session fields, the window switcher.</summary>
internal static class MakyxUsabilityTests
{
    private static MakyxRun Run(string id, string project, MakyxStatus status, string? window = null, string? shortName = null, string? role = null) =>
        new(id, id, "session", "Titel " + id, status, project, "rechner-a", null, null, null, null, "", null,
            window is null ? MakyxTerminal.None("x") : new MakyxTerminal(true, null, "rechner-a", null, "makyx", "makyx", window), shortName, role);

    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("Kurznamen: gültig, normalisiert, Rückfall auf Fenstername und Kennung", () =>
        {
            check(MakyxNames.IsValid("ingest-api") && MakyxNames.IsValid("worker-1") && !MakyxNames.IsValid("-x") && !MakyxNames.IsValid("Gross") && !MakyxNames.IsValid(new string('a', 21)) && !MakyxNames.IsValid(null), "Regeln");
            check(MakyxNames.Normalize("  Ingest API ") == "ingest-api" && MakyxNames.Normalize("Größe_Prüfen") == "groesse-pruefen" && MakyxNames.Normalize("a--b__c") == "a-b-c", "normalisiert: " + MakyxNames.Normalize("Größe_Prüfen"));
            check(MakyxNames.Normalize("!!!") is null && MakyxNames.Normalize(" ") is null && MakyxNames.Normalize("sehr-langer-name-fuer-eine-session")!.Length <= 20, "leer und zu lang");
            check(MakyxNames.ShortName(Run("S-1", "p", MakyxStatus.Working, "worker-c64d", "ingest-api")) == "ingest-api", "kurzname zuerst");
            check(MakyxNames.ShortName(Run("S-1", "p", MakyxStatus.Working, "worker-c64d")) == "worker-c64d", "dann der Fenstername");
            check(MakyxNames.ShortName(Run("S-1", "p", MakyxStatus.Working, "@3")) == "S-1" && MakyxNames.ShortName(Run("S-1", "p", MakyxStatus.Working, "2")) == "S-1", "Fenster-Id und Index sind keine Namen");
            check(MakyxNames.RowText(Run("S-1", "p", MakyxStatus.Working, null, "ingest-api", "worker")) == "ingest-api · Worker", "Zeile mit Rolle");
        });

        test("Makyx-API: kurzname, rolle, projekt_name, tmux-Objekt, RAM, zuletzt_aktiv", () =>
        {
            const string json = "{\"laeufe\":[{\"id\":\"S-1\",\"art\":\"session\",\"status\":\"wartet\",\"projekt\":\"p-7\",\"projekt_name\":\"Beispiel\",\"kurzname\":\"ingest-api\",\"rolle\":\"worker\","
                + "\"rechner\":\"rechner-a\",\"tmux\":{\"socket\":\"makyx\",\"session\":\"makyx\",\"fenster_index\":2,\"fenster_name\":\"ingest-api\"}},"
                + "{\"id\":\"S-2\",\"art\":\"session\",\"status\":\"arbeitet\",\"kurzname\":\"Böse Name\",\"tmux\":{\"socket\":\"makyx\",\"session\":\"makyx\",\"fenster_index\":3}}]}";
            var runs = MakyxJson.ParseRuns(json, out _);
            check(runs[0].ShortName == "ingest-api" && runs[0].Role == "worker" && runs[0].ProjectName == "Beispiel", "neue Felder gelesen");
            check(runs[0].Terminal is { Available: true, TmuxSocket: "makyx", TmuxSession: "makyx", TmuxWindow: "ingest-api" }, "tmux-Objekt ersetzt terminal");
            check(runs[1].ShortName is null && runs[1].Terminal.TmuxWindow == "3", "ungültiger Kurzname verworfen, Index als Fenster");
            check(MakyxControl.TerminalName(runs[0]) == "Beispiel · ingest-api", "Tab-Name");
            var machine = MakyxJson.ParseMachines("{\"rechner\":[{\"id\":\"r1\",\"name\":\"rechner-a\",\"verbunden\":true,\"ram_frei_mb\":10035,\"ram_gesamt_mb\":16384}]}").Single();
            check(MakyxControl.MachineLabel(machine) == "rechner-a · RAM 9,8 / 16 GB frei", "RAM in der Auswahl: " + MakyxControl.MachineLabel(machine));
            var project = MakyxJson.ParseProjects("{\"projekte\":[{\"id\":\"p-7\",\"name\":\"Beispiel\",\"zuletzt_aktiv\":\"2026-01-02T10:00:00Z\",\"modell\":\"modell-x\",\"instanzen_laufend\":2}]}").Single();
            check(project.LastActive == DateTimeOffset.Parse("2026-01-02T10:00:00Z") && project.DefaultModel == "modell-x", "Projekt: zuletzt aktiv und Modell");
            check(MakyxControl.ProjectDetail(project, DateTimeOffset.Parse("2026-01-02T10:33:00Z")) == "2 Läufe aktiv · zuletzt vor 33 min", "Projektzeile: " + MakyxControl.ProjectDetail(project, DateTimeOffset.Parse("2026-01-02T10:33:00Z")));
        });

        test("Neue Session: Name, Arbeitsordner und erste Nachricht nur, wenn gesetzt; Umbenennen", () =>
        {
            check(MakyxControl.SessionBody("p-1", null, null, null) == "{\"projekt\":\"p-1\",\"agent\":\"claude\",\"modus\":\"terminal\"}", "ohne neue Felder wie bisher");
            check(MakyxControl.SessionBody("p-1", null, null, null, "Ingest API", MakyxFolder.Main, " Lies die Spezifikation ")
                == "{\"projekt\":\"p-1\",\"agent\":\"claude\",\"modus\":\"terminal\",\"name\":\"ingest-api\",\"arbeitsordner\":\"haupt\",\"erste_nachricht\":\"Lies die Spezifikation\"}", "mit allem");
            check(MakyxControl.SessionBody("p-1", null, null, null, "!!", MakyxFolder.Worktree).Contains("\"arbeitsordner\":\"worktree\"") && !MakyxControl.SessionBody("p-1", null, null, null, "!!").Contains("name"), "leerer Name fällt weg");
            check(MakyxControl.RenameBody("Neuer Name") == "{\"name\":\"neuer-name\"}", "Umbenennen");
            bool refused = false; try { MakyxControl.RenameBody(" "); } catch (ArgumentException) { refused = true; }
            check(refused, "Umbenennen ohne Namen abgelehnt");
        });

        test("Eigene tmux-Ansicht je Tab: Befehl, Namen, Ablehnung", () =>
        {
            string view = MakyxTmux.NewViewName();
            check(MakyxTmux.IsOwnView(view) && MakyxTmux.IsView(view) && MakyxTmux.IsView("ansicht-1903c2e2") && !MakyxTmux.IsView("makyx") && !MakyxTmux.IsOwnView("tm-xyz"), "Namen der Ansichten");
            string command = SshCommand.MakyxViewCommand("makyx", "makyx", "worker-c64d", "tm-0123abcd");
            check(command == "tmux -L 'makyx' new-session -A -t 'makyx' -s 'tm-0123abcd' \\; set-option -t 'tm-0123abcd' destroy-unattached on \\; select-window -t 'tm-0123abcd:worker-c64d'", "Befehl: " + command);
            check(SshCommand.MakyxViewCommand("makyx", "$1", "@3", "tm-0123abcd").Contains("-t '$1'") && SshCommand.MakyxViewCommand("makyx", "$1", "@3", "tm-0123abcd").EndsWith("'tm-0123abcd:@3'"), "Ids erlaubt");
            var profile = new Profile { Name = "rechner-a", Destination = "rechner-a" };
            check(SshCommand.MakyxViewAttachArguments(profile, "makyx", "makyx", "2", "tm-0123abcd").Contains("-tt"), "interaktiv");
            foreach (var (s, se, w, v) in new[] { ("ma kyx", "makyx", "a", "tm-0123abcd"), ("makyx", "$(id)", "a", "tm-0123abcd"), ("makyx", "makyx", "a;b", "tm-0123abcd"), ("makyx", "makyx", "a", "ansicht-1") })
            {
                bool refused = false;
                try { SshCommand.MakyxViewCommand(s, se, w, v); } catch (ArgumentException) { refused = true; }
                check(refused, $"abgelehnt: {s}/{se}/{w}/{v}");
            }
            // The view of a tab is a grouped session: its windows appear once, under the real session.
            string output = string.Join('\n', "$1\t@1\t0\t0\tmakyx\tuebersicht\tsleep", "$1\t@2\t1\t1\tmakyx\tworker-c64d\tclaude", "$5\t@2\t1\t1\ttm-0123abcd\tworker-c64d\tclaude");
            var sessions = MakyxTmux.Sessions(TmuxWindowParser.Parse(output));
            check(sessions.Count == 1 && sessions[0].Windows.Count == 1 && sessions[0].Windows[0].SessionName == "makyx", "eigene Ansicht ohne Doppel");
        });

        test("Seitenleiste: Läufe nach Projekt gruppiert, aktive zuerst, wartende oben", () =>
        {
            var runs = new[]
            {
                Run("S-1", "p-7", MakyxStatus.Working, "a", "aaa"), Run("S-2", "p-7", MakyxStatus.Waiting, "b", "zzz"), Run("L-1", "rechner", MakyxStatus.Failed),
                Run("L-2", "rechner", MakyxStatus.Done), Run("S-3", "", MakyxStatus.Working, "c")
            };
            var projects = new[] { new MakyxProject("p-7", "Beispiel", null, null, null, 2, 0, ""), new MakyxProject("p-9", "Leer", null, null, null, 0, 0, "") };
            var groups = MakyxGrouping.Groups(runs, projects);
            check(groups[0].Name == "Beispiel" && groups[0].Active.Select(r => r.Id).SequenceEqual(["S-2", "S-1"]) && groups[0].Waiting == 1, "Projekt mit Aktiven zuerst, Wartende oben");
            check(groups[1].Key == "" && groups[1].Name == "freie Session", "freie Sessions als eigene Gruppe");
            var calc = groups.Single(g => g.Key == "rechner");
            check(calc.Active.Count == 0 && calc.Failed == 1 && calc.Recent == 2 && MakyxGrouping.Summary(calc) == "1 fehlgeschl. · 2", "beendete Läufe nur gezählt: " + MakyxGrouping.Summary(calc));
            check(groups.Any(g => g.Name == "Leer" && MakyxGrouping.Summary(g) == "keine"), "Projekt ohne Läufe bleibt sichtbar (für +)");
            check(MakyxGrouping.Recent(runs, 1).Count == 1, "Zuletzt begrenzt");
        });

        test("Fenster wechseln: Makyx-Fenster mit Projekt und Status, Suche über alle Wörter", () =>
        {
            var windows = TmuxWindowParser.Parse(string.Join('\n', "$1\t@1\t0\t0\tmakyx\tuebersicht\tbash", "$1\t@2\t2\t1\tmakyx\tworker-c64d\tclaude"));
            var sessions = MakyxTmux.Sessions(windows);
            var run = Run("S-1", "p-7", MakyxStatus.Waiting, "worker-c64d") with { ProjectName = "Beispiel" };
            var entries = TmuxSwitcher.MakyxEntries("makyx", sessions, [run], w => w.WindowId == "@2");
            check(entries.Count == 2 && entries.Any(e => e.Group == "Makyx · Beispiel" && e.Status == "wartet" && e.Here) && entries.Any(e => e.Group == "Makyx · makyx" && e.Status == ""), "Gruppen und Status");
            var plain = TmuxSwitcher.PlainEntries(TmuxWindowParser.Group(TmuxWindowParser.Parse("$3\t@7\t1\t1\tclaude\tarchyx\tclaude")), _ => false);
            var all = entries.Concat(plain).ToList();
            check(TmuxSwitcher.Filter(all, "").Count == 3 && TmuxSwitcher.Filter(all, "worker WARTET").Count == 0 && TmuxSwitcher.Filter(all, "beispiel c64d").Count == 1 && TmuxSwitcher.Filter(all, "archyx").Count == 1, "Suche");
        });
    }
}
