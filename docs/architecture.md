# Architektur

Stand: 25.10.2026

WPF stellt die Windows-Oberfläche bereit. Eine WebView2-Instanz enthält je geöffnetem Profil eine xterm.js-Instanz. Session-Wechsel blenden Flächen um; SSH-Prozesse bleiben erhalten.

Tastatur → xterm.onData → WebView2-Nachricht → ConPTY-Eingabepipe → OpenSSH → Linux-PTY → tmux.

ResizeObserver / Schriftgröße → FitAddon → Spalten und Zeilen → ResizePseudoConsole → OpenSSH → Remote-PTY. Verdeckte Terminals werden bei Auswahl angepasst.

## Prozessverwaltung

Windows OpenSSH erhält bekannte Hosts, SSH-Agent, Schlüssel und Config-Auswertung. Es wird keine lokale Shell zur Ausführung gestartet. CreateProcess-Argumente werden gequotet; Remote-Befehle bewusst an die entfernte Shell übergeben.

ConPTY benutzt getrennte Arbeiter für Lesen, Schreiben und Beenden. Eine begrenzte Ausgabewarteschlange setzt auf der nativen Seite Backpressure um. Beim Beenden wird die Ausgabe weiter geleert.

STARTF_USESTDHANDLES mit leeren Handles verhindert vererbte umgeleitete Standard-Handles. TERM wird auf xterm-256color gesetzt. Der im ersten Prototyp beobachtete tmux-Fehler durch einen geerbten ungeeigneten Terminaltyp ist per Integrationstest abgesichert.

## tmux-Auswahl

Eine separate SSH-Abfrage führt tmux list-sessions aus. RemoteCommand wird überschrieben, PTY deaktiviert und BatchMode verhindert unsichtbare Passwortdialoge. Auswahlziele sind validierte stabile Session-IDs ($0, $1, …), keine Anzeigenamen. Erst nach Auswahl wird die vorherige Verbindung getrennt.

## tmux-Fenstermenü

Eine zweite nicht interaktive Abfrage führt `tmux list-windows -a -F` aus (Session-ID, Fenster-ID, Index, aktiv, Sessionname, Fenstername, laufendes Programm). Feste Felder stehen vorn, Freitextfelder hinten; Tabs im Fensternamen verschieben deshalb keine IDs. Der Parser liegt in `TmuxWindowParser` (Core), das Menü baut `TmuxMenu`.

Wechsel bei verbundenem Server: zwei Schritte mit 150 ms Pause. Zuerst Ctrl+B und `:`, dann `switch-client -t '$id:@id'` und Enter. tmux öffnet den Prompt erst nach der Verarbeitung des `:`; in einem Schreibvorgang gesendete Folgezeichen landeten im Programm des Fensters (am echten tmux 3.6 beobachtet). Bei nicht verbundenem Server wird mit `tmux attach-session -t '$id:@id'` verbunden. Ob eine Verbindung an einer Session hängt, ergibt sich aus der zuletzt gewählten ID, dem tmux-Namen des Profils oder dem RemoteCommand aus `ssh -G`.

## Konfigurationsdatei

`ConfigFile` (Core) baut und liest das `.tmconfig`-Format (`format`, `formatVersion`, `appVersion`, `exported`, `encryption`, `content` oder `data`). `ConfigImporter` erzeugt aus Datei und aktuellem Stand einen Plan und wendet ihn rein funktional an; `ConfigBackup`, `SshConfigBlocks` und `ConfigCheck` kümmern sich um Sicherungen, Host-Blöcke und die Prüfung nach dem Import. Die Dialoge und Dateizugriffe liegen in `MainWindow.Config.cs`.

## WebView-Grenze

Alle Terminalressourcen liegen lokal. Navigation ist auf die interne Seite begrenzt; neue Fenster, Downloads und Berechtigungen werden abgewiesen. Nachrichten werden nur von der internen Seite angenommen. SSH-Ausgabe wird als Terminaltext übergeben, nie als HTML. OSC52 ist deaktiviert.

## Technologie und Design

C#/.NET 8 wurde wegen des vorhandenen SDKs gewählt. Vor längerfristiger Veröffentlichung auf eine dann unterstützte LTS-Version aktualisieren. WPF bildet den Rahmen, JavaScript die Terminal-Brücke. Abhängigkeiten sind versioniert.

Mockup und Icon-Vergleich liegen in design/. Freigegeben ist Icon 3 „Sessions“. PNG und ICO liegen unter src/TerminalMgr.App/Assets.
