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

Clipboard-Liste (rechts, neueste oben):

- Alles, was in Sessions markiert oder in einem anderen Programm kopiert wird, landet automatisch oben und ist aktiv. Die Herkunft ist der Sessionname oder „Windows“. Einträge von Passwortmanagern (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0) werden nicht übernommen.
- Erneut kopierter Text erzeugt kein Duplikat, sondern rückt nach oben.
- Klick macht einen Eintrag aktiv (der nächste Rechtsklick fügt ihn ein), Doppelklick fügt ihn sofort in die aktive Session ein. Die Vorschau zeigt zwei Zeilen; längere Texte enden mit „…“ und zeigen z. B. „42 Zeilen“. Der Tooltip zeigt einen größeren Ausschnitt.
- „Anheften“ erhält einen Eintrag über Neustarts. Ungeheftete Einträge bleiben im Speicher, höchstens 100 (einstellbar unter *Einstellungen*).

Einfügen nutzt Bracketed Paste. Die App sendet keinen zusätzlichen Enter-Tastendruck. Remote-OSC52 ist deaktiviert; tmux-interne Copy-Mode-Puffer werden noch nicht automatisch in den lokalen Manager übernommen.

Einstellungen und angeheftete Texte liegen in %LOCALAPPDATA%\TerminalMgr\settings.json, also außerhalb des Installationsordners und bleiben bei Updates erhalten. SSH-Schlüssel und Passwörter verwaltet OpenSSH. Angeheftete Texte werden als Klartext gespeichert.

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

Browsertests verwenden den installierten Microsoft Edge. Die Konsolentests starten echte lokale ConPTY-Kindprozesse. Der optionale lokale Integrationstest mit -- --ssh-smoke nutzt den SSH-Alias linuxdev; er läuft nicht in CI.

Eigenständiges Windows-x64-Paket:

~~~powershell
dotnet publish src/TerminalMgr.App -c Release -r win-x64 --self-contained true -o artifacts/TerminalMgr-win-x64
~~~

## Struktur

- TerminalMgr.Core: Profile, SSH-Argumente, Import, Einstellungen, tmux-Antwortparser.
- TerminalMgr.Windows: ConPTY und SSH-Abfrage für tmux.
- TerminalMgr.App: WPF, WebView2-Brücke und lokales xterm.js.
- web: gepinnte npm-Abhängigkeiten, Bündelung und Browsertests.
- docs: Architektur, Teststand und Mockup.

## Noch offen

Installer/signierte Releases, Windows-Anmeldungs-Autostart, Wiederverbindung nach Netzabbrüchen, konfigurierbare App-Shortcuts, automatische tmux-Copy-Mode-Übernahme und native tmux-Fensterliste. Die Fensterwahl ist bereits über Ctrl+B w erreichbar.

Dies ist ein nutzbarer Prototyp. „SSH läuft“ bezeichnet den Clientprozess; daraus wird keine abgeschlossene Authentifizierung abgeleitet.
