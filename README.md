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

Die tmux-Auswahl verwendet eine separate nicht interaktive SSH-Abfrage mit SSH-Key/Agent. Sie verändert die laufende Session nicht. Erst „Session öffnen“ trennt die bisherige Client-Verbindung und hängt eine neue an die Auswahl an. Die Auswahl gilt für diese Verbindung; das gespeicherte Startprofil bleibt bestehen. Das direkte Fenstermenü setzt Ctrl+B als tmux-Präfix voraus.

### Clipboard

- Terminaltext markieren und Ctrl+Shift+C: in Windows kopieren und mit Serverherkunft sammeln.
- Ctrl+Shift+V: Windows-Zwischenablage ins Terminal einfügen.
- „Aus Zwischenablage“: einen Windows-Eintrag explizit übernehmen.
- „Einfügen“ oder Text einer Karte ziehen: in die aktive Session übernehmen.
- „Anheften“: Eintrag über Neustarts erhalten. Ungehefteter Verlauf bleibt nur im Speicher.
- Bei aktivem tmux-Mausmodus zur lokalen Textauswahl die Umschalttaste verwenden.

Einfügen nutzt Bracketed Paste. Die App sendet keinen zusätzlichen Enter-Tastendruck. Remote-OSC52 ist deaktiviert; tmux-interne Copy-Mode-Puffer werden noch nicht automatisch in den lokalen Manager übernommen.

Einstellungen und angeheftete Texte liegen in %LOCALAPPDATA%\TerminalMgr\settings.json. SSH-Schlüssel und Passwörter verwaltet OpenSSH. Angeheftete Texte werden als Klartext gespeichert.

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
