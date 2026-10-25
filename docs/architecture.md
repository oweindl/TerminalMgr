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

## WebView-Grenze

Alle Terminalressourcen liegen lokal. Navigation ist auf die interne Seite begrenzt; neue Fenster, Downloads und Berechtigungen werden abgewiesen. Nachrichten werden nur von der internen Seite angenommen. SSH-Ausgabe wird als Terminaltext übergeben, nie als HTML. OSC52 ist deaktiviert.

## Technologie und Design

C#/.NET 8 wurde wegen des vorhandenen SDKs gewählt. Vor längerfristiger Veröffentlichung auf eine dann unterstützte LTS-Version aktualisieren. WPF bildet den Rahmen, JavaScript die Terminal-Brücke. Abhängigkeiten sind versioniert.

Mockup und Icon-Vergleich liegen in design/. Freigegeben ist Icon 3 „Sessions“. PNG und ICO liegen unter src/TerminalMgr.App/Assets.
