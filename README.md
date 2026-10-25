# TerminalMgr

Windows-Arbeitsplatz für mehrere SSH-Verbindungen mit tmux, schnellem Session-Wechsel und angedocktem Clipboard.

<img src="src/TerminalMgr.App/Assets/TerminalMgr.png" width="128" alt="TerminalMgr">

## Erste Version

- OpenSSH-Config automatisch beim ersten Start oder manuell importieren. Explizite Host-Aliase und Includes werden erkannt; Host/Match, Schlüssel, ProxyJump und RemoteCommand wertet Windows OpenSSH aus.
- Mehrere Verbindungen parallel offen halten und über Serverliste oder Alt+1 bis Alt+9 wechseln.
- Ctrl+B und weitere tmux-Tasten unverändert weiterreichen.
- Fenster, Seitenbereiche und Schriftgröße ändern: neue Zeilen/Spalten werden über ConPTY und OpenSSH bis zum Linux-PTY weitergegeben.
- Pro Profil beim App-Start automatisch verbinden. Startmodi: SSH-Config, tmux erstellen/anhängen oder eigener Remote-Befehl.
- Rechtsklick auf Server → **tmux-Session auswählen …**: vorhandene Sessions mit Fensteranzahl und aktiven Verbindungen abfragen und öffnen.
- Rechtsklick → **tmux-Fensterwahl öffnen** sendet Ctrl+B, dann w.
- Rechtsklick zeigt darunter **alle tmux-Sessions des Servers mit ihren Fenstern** („Index: Name“, bei abweichendem Programm mit „(vim)“; ● markiert das aktive Fenster, „verbunden“ die Session, an der TerminalMgr hängt). Das Menü öffnet sofort, die Liste kommt per separater SSH-Abfrage (Key/Agent, kein Passwortdialog, 10 s Zeitlimit) und funktioniert auch bei nicht verbundenem Server. Bei mehr als etwa 25 Einträgen gibt es ein Untermenü je Session.
- Klick auf ein Fenster wechselt den eigenen tmux-Client dorthin (auch in eine andere Session, die bisherige läuft weiter). Ist der Server nicht verbunden, wird verbunden und direkt an dieses Fenster angehängt. Es werden Session- und Fenster-IDs verwendet, nicht Namen.
- Clipboard mit Herkunft, Suche, Anheften, gezieltem Einfügen und Drag-and-drop.
- Gewähltes Icon: Vorschlag 3 „Sessions“, in sieben Auflösungen von 16 bis 256 Pixeln.

## Starten

Windows 10 1809 oder neuer / Windows 11, Windows OpenSSH-Client und Microsoft Edge WebView2 Runtime werden benötigt.

Das eigenständige Paket startet über TerminalMgr.App.exe. Den gesamten Paketordner behalten. Ein lokaler Build benötigt zusätzlich die .NET-8-Desktop-Runtime (im SDK enthalten).

Beim ersten Start wird %USERPROFILE%\.ssh\config importiert. Profile werden zunächst nicht automatisch verbunden. Im Profil „Beim Start von TerminalMgr automatisch verbinden“ aktivieren.

### Startmodi

| Modus | Verhalten |
| --- | --- |
| SSH-Config | Bestehende Einstellungen inklusive RequestTTY und RemoteCommand gelten. |
| tmux | tmux new-session -A -s NAME; optionaler Befehl läuft nur bei einer neuen Session. |
| Remote-Befehl | Läuft bei jedem Verbindungsaufbau. Endet der Befehl, endet SSH. Für eine anschließende Shell z. B.: dein-befehl; exec bash -l. |

Bei tmux und Remote-Befehl wird RemoteCommand gezielt überschrieben. Der Kindprozess erhält TERM=xterm-256color.

Die tmux-Auswahl verwendet eine separate nicht interaktive SSH-Abfrage mit SSH-Key/Agent. Sie verändert die laufende Session nicht. Erst „Session öffnen“ trennt die bisherige Client-Verbindung und hängt eine neue an die Auswahl an. Die Auswahl gilt für diese Verbindung; das gespeicherte Startprofil bleibt bestehen. Das direkte Fenstermenü und der Wechsel per Klick setzen Ctrl+B als tmux-Präfix voraus. Der Wechsel sendet Präfix und „:“, wartet kurz, bis tmux den Befehlsprompt geöffnet hat, und gibt dann `switch-client -t '$session:@fenster'` ein; so landen keine Zeichen im laufenden Programm.

### Clipboard

Maus wie bei PuTTY:

- Links ziehen markiert (Doppelklick: Wort, Dreifachklick: Zeile). **Rechtsklick mit Markierung** kopiert in die Windows-Zwischenablage und in die Liste und hebt die Markierung auf. **Rechtsklick ohne Markierung** fügt den aktiven Listeneintrag in die Session ein.
- Nachgestellte Leerzeichen je Zeile (Terminalbreite) werden beim Kopieren entfernt.
- Der Rechtsklick im Terminal gehört immer der App und erreicht nie die Session. Bei aktivem Maus-Tracking (tmux `mouse on`, vim, htop) markiert **Umschalt + Ziehen** trotzdem lokal.
- Ctrl+Shift+C kopiert die Markierung wie der Rechtsklick, Ctrl+Shift+V fügt die Windows-Zwischenablage ein.
- **Markieren in Claude Code oder tmux:** Programme mit eigener Maus-Unterstützung (Claude Code, tmux mit `mouse on`) markieren selbst; xterm.js hat dann keine Markierung, ein Rechtsklick würde einfügen. Beim Loslassen kopieren sie aber in den tmux-Puffer, und tmux meldet das per OSC 52 an das Terminal. TerminalMgr übernimmt diese Schreibanfragen: Text landet oben in der Clipboard-Liste (Herkunft Sessionname, aktiv) und in der Windows-Zwischenablage, der nächste Rechtsklick fügt ihn ein. Lesen der Zwischenablage durch ein Programm ist nie möglich (Leseanfragen werden nicht beantwortet). Abschaltbar unter *Einstellungen* (Vorgabe an). Geprüft mit tmux 3.6 und der tmux-Vorgabe `set-clipboard external`; kommt bei Ihrer Konfiguration nichts an, in `~/.tmux.conf` `set -s set-clipboard on` eintragen. Zur Fehlersuche zeigt `TERMINALMGR_DEBUG=1` bei jedem Rechtsklick in der Statuszeile Markierung und Maus-Tracking-Modus.

Clipboard-Liste (rechts, neueste oben):

- Alles, was in Sessions markiert oder in einem anderen Programm kopiert wird, landet automatisch oben und ist aktiv. Die Herkunft ist der Sessionname oder „Windows“. Einträge von Passwortmanagern (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0) werden nicht übernommen.
- Erneut kopierter Text erzeugt kein Duplikat, sondern rückt nach oben.
- Klick macht einen Eintrag aktiv (der nächste Rechtsklick fügt ihn ein), Doppelklick fügt ihn sofort in die aktive Session ein. Die Vorschau zeigt zwei Zeilen; längere Texte enden mit „…“ und zeigen z. B. „42 Zeilen“. Der Tooltip zeigt einen größeren Ausschnitt.
- „Anheften“ erhält einen Eintrag über Neustarts. Ungeheftete Einträge bleiben im Speicher, höchstens 100 (einstellbar unter *Einstellungen*).

Einfügen nutzt Bracketed Paste. Die App sendet keinen zusätzlichen Enter-Tastendruck. Remote-OSC52 ist deaktiviert; tmux-interne Copy-Mode-Puffer werden noch nicht automatisch in den lokalen Manager übernommen.

Einstellungen und angeheftete Texte liegen in %LOCALAPPDATA%\TerminalMgr\settings.json, also außerhalb des Installationsordners und bleiben bei Updates erhalten. SSH-Schlüssel und Passwörter verwaltet OpenSSH. Angeheftete Texte werden als Klartext gespeichert.

### Diktat

Gesprochener Text landet in einem Diktatfenster über dem Terminal, ist dort korrigierbar und geht dann in die Session. Gedacht für Prompts an Claude Code und andere Programme in tmux.

- **Ctrl+Y** (oder Button „Diktat“ rechts unten über der Statusleiste) öffnet das Fenster und startet die Aufnahme. Ctrl+Y wird von der App abgefangen und erreicht die Session nie. Die Belegung ist unter *Einstellungen* änderbar; in bash/readline ist Ctrl+Y „yank“, das mit der Standardbelegung nicht erreichbar ist.
- Zielsession ist die beim Öffnen aktive; ihr Name steht im Titel. Ein späterer Serverwechsel ändert das Ziel nicht.
- Beim Sprechen erscheint ein grauer, kursiver Zwischenstand. Nach 1,5 s Stille (einstellbar) endet die Aufnahme; der endgültige Text ersetzt den Zwischenstand. Ohne Sprache endet sie nach 5 s und es wird nichts transkribiert. Erneut Ctrl+Y setzt fort, neuer Text kommt an die Cursorposition. Läuft die Aufnahme, beendet Ctrl+Y sie sofort.
- **Enter** fügt den Text ein (ohne Enter), **Ctrl+Enter** fügt ein und führt aus, **Shift+Enter** ist ein Zeilenumbruch, **Esc** bricht zuerst die Aufnahme ab und verwirft danach das Diktat. Eingefügt wird per Bracketed Paste; Steuerzeichen außer Zeilenumbruch und Tab werden entfernt, „Ausführen“ sendet danach genau ein Enter.
- Die Erkennung läuft lokal und offline mit whisper.cpp über Whisper.net (Grafikkarte per Vulkan, sonst CPU). Audio bleibt im Speicher und wird nicht gespeichert.
- Das Modell (Vorgabe `large-v3-turbo-q5_0`, ca. 0,6 GB; leichter: `small-q5_1`) liegt unter `%LOCALAPPDATA%\TerminalMgr\models`, nicht im Installationsordner. Der Download startet erst nach Rückfrage beim ersten Diktat, mit Fortschrittsanzeige und Abbruch.
- Einstellungen: Tastenkürzel, Mikrofon, Modell, Sprache (Deutsch, Englisch, automatisch), Fachbegriffe für die Erkennung (Initial-Prompt), Stilledauer und Pegelschwelle.

### Tastenkürzel, Wiederverbindung, Start mit Windows

- **Tastenkürzel** (*Einstellungen*): Diktat (Vorgabe Ctrl+Y), Markierung kopieren (Ctrl+Shift+C), Zwischenablage einfügen (Ctrl+Shift+V) und der Modifikator für den Sessionwechsel mit 1 bis 9 (Vorgabe Alt; alternativ Ctrl+Alt, Alt+Shift, Ctrl+Shift). Die Kürzel gelten, bevor die Session die Taste sieht. Eine Kombination braucht Ctrl oder Alt (nur F1 bis F12 gehen allein). Ctrl+B bleibt für tmux reserviert, doppelte Belegungen werden beim Speichern abgelehnt. Ein Hinweis erscheint, wenn eine Kombination in bash/readline etwas bedeutet (Ctrl+Y ist „yank“).
- **Automatische Wiederverbindung** (Profil, standardmäßig aus): Geht die Verbindung verloren (SSH-Exit 255 oder bei Windows OpenSSH -1 für „Verbindung vom Server geschlossen“), verbindet TerminalMgr nach 2, 4, 8, 16 und danach 30 Sekunden neu, höchstens zehnmal. Eine Verbindung, die länger als eine Minute hielt, setzt den Zähler zurück. Normale Enden (exit, tmux-Detach, Fehler des Startbefehls) und „Trennen“ lösen nichts aus. Eine wiederverbundene Session nimmt dem Benutzer die Auswahl nicht weg. Wurde per tmux-Auswahl an eine bestimmte Session angehängt, hängt der Wiederaufbau wieder an dieselbe.
- **Start mit Windows** (*Einstellungen*, standardmäßig aus): trägt die EXE unter `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` ein (ohne Administratorrechte, sichtbar unter Windows-Einstellungen > Apps > Autostart). Es registriert sich nur die installierte `TerminalMgr.App.exe`; verschiebt man die App, passt der nächste Start den Eintrag an.

## Entwickeln

.NET 8 SDK, Node.js und Windows:

~~~powershell
cd web
npm ci
npm run bundle
cd ..
dotnet build TerminalMgr.sln -c Release
dotnet run --project src/TerminalMgr.App -c Release
~~~

Tests:

~~~powershell
dotnet run --project tests/TerminalMgr.Tests -c Release
dotnet run --project tests/TerminalMgr.UiTests -c Release
cd web
npm test
~~~

Browsertests verwenden den installierten Microsoft Edge. Die Konsolentests starten echte lokale ConPTY-Kindprozesse. Zwei optionale lokale Integrationstests laufen nicht in CI:

~~~powershell
# echte SSH-Prüfungen auf einem eigenen Test-Host (SSH-Alias in TERMINALMGR_SSH_TEST_HOST, der Server braucht tmux; Anmeldung per Key oder Agent): Terminaltyp, Größe, tmux-Fensterliste, Anhängen und Wechseln (legt Test-Sessions tmgr-ta/tmgr-tb an und räumt sie auf)
$env:TERMINALMGR_SSH_TEST_HOST = "mein-alias"
dotnet run --project tests/TerminalMgr.Tests -c Release -- --ssh-smoke
# echtes whisper.cpp mit Windows-Sprachausgabe als Testaudio; lädt das Modell einmalig nach %TEMP% (tiny, small-q5_1, ...)
dotnet run --project tests/TerminalMgr.Tests -c Release -- --whisper-smoke=small-q5_1
~~~

Eigenständiges Windows-x64-Paket:

~~~powershell
dotnet publish src/TerminalMgr.App -c Release -r win-x64 --self-contained true -o artifacts/TerminalMgr-win-x64
~~~

Das Repository enthält keine eigenen Serverdaten (keine settings.json, SSH-Config, Hostnamen, Schlüssel); `.gitignore` hält Einstellungen, Exportdateien (`*.tmconfig`), SSH-Config-Kopien, `*.pem` und `id_*` fern. `build.yml` prüft bei jedem Push die gesamte Git-Historie mit gitleaks und bricht bei einem Fund ab (`.gitleaks.toml` nimmt nur das gebündelte xterm.js aus). Lokal: gitleaks von https://github.com/gitleaks/gitleaks/releases laden und `gitleaks git --log-opts="--all" --redact .` ausführen.

### Lokal installieren

~~~powershell
.\scripts\Install-Local.ps1
~~~

Das Skript baut die Terminal-Assets (`npm ci`, `npm run bundle`), führt alle Tests aus und bricht bei einem Fehler ab, veröffentlicht eigenständig für win-x64 in einen temporären Ordner, beendet eine laufende TerminalMgr aus dem Installationsordner (es fragt vorher, wenn SSH-Verbindungen offen sind; `-Force` fragt nicht), ersetzt den Inhalt von `%LOCALAPPDATA%\Programs\TerminalMgr` und legt die Verknüpfung `TerminalMgr.lnk` auf dem Desktop (auch bei OneDrive-Desktop) und im Startmenü an oder aktualisiert sie. Am Ende nennt es Version und Pfad. Die Verknüpfungen zeigen immer auf denselben Pfad und bleiben nach jedem Update gültig. Einstellungen, angeheftete Clipboard-Einträge und Modelle liegen in `%LOCALAPPDATA%\TerminalMgr` und bleiben unberührt.

Schalter: `-Force` (nicht nachfragen, eine nicht reagierende App beenden), `-SkipTests` (nur zum schnellen Neu-Installieren eines bereits getesteten Stands), `-Start` (App danach starten).

### Version und Release

In `Directory.Build.props` steht nur Major.Minor (`MajorMinor`, jetzt 0.2). Die Version eines Releases ist Major.Minor plus die Laufnummer des Workflows als Patch, z. B. 0.2.14; lokale Builds heißen 0.2.0. Die Version steht in der EXE (`FileVersion`, `InformationalVersion` mit Commit) und erscheint in der Titelzeile und unter *Einstellungen*. Major.Minor erhöht man von Hand in `Directory.Build.props`.

Jeder Push nach `main` erzeugt nach grünen Tests (Build, Konsolen-, WPF-, Browsertests, gitleaks) automatisch ein Release: der Workflow `.github/workflows/build.yml` setzt den Tag `vX.Y.N` selbst, packt `TerminalMgr-win-x64-<version>.zip` und `TerminalMgr-win-x64-<version>.zip.sha256` und schreibt Release-Notes aus den Commit-Nachrichten seit dem letzten Release. Feature-Branches und Pull Requests bauen und testen nur. Der Tag löst keinen zweiten Lauf aus. Builds gehören nur in Releases, nie ins Repository.

## Struktur

- TerminalMgr.Core: Profile, SSH-Argumente, Import, Einstellungen, tmux-Antwortparser.
- TerminalMgr.Windows: ConPTY und SSH-Abfrage für tmux.
- TerminalMgr.App: WPF, WebView2-Brücke und lokales xterm.js.
- web: gepinnte npm-Abhängigkeiten, Bündelung und Browsertests.
- docs: Architektur, Teststand und Mockup.

## Noch offen

Installer/signierte Releases, Windows-Anmeldungs-Autostart, Wiederverbindung nach Netzabbrüchen, konfigurierbare App-Shortcuts, automatische tmux-Copy-Mode-Übernahme und native tmux-Fensterliste. Die Fensterwahl ist bereits über Ctrl+B w erreichbar.

Dies ist ein nutzbarer Prototyp. „SSH läuft“ bezeichnet den Clientprozess; daraus wird keine abgeschlossene Authentifizierung abgeleitet.
