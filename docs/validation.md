# Validierung

## Version 0.2.0 (25.10.2026)

Automatisiert, lokal grün: 62 Konsolen-/Core-Prüfungen (+ 7 mit `--ssh-smoke`, + 2 mit `--whisper-smoke`), 6 WPF-Prüfungen, 15 Browserprüfungen in Edge. CI baut und testet bei jedem Push.

- **Maus und Clipboard:** Rechtsklick kopiert/fügt ein, Markierung wird aufgehoben, Rechtsklick erreicht bei Maus-Tracking die Session nie, Umschalt+Ziehen markiert (Playwright). Clipboard-Liste mit aktivem Eintrag, Duplikaten, Limit (Core). Echte Windows-Zwischenablage: Änderungsmeldung, Textabzug, Passwortmanager-Kennzeichen (WPF-Test, stellt den Inhalt wieder her).
- **tmux-Fenstermenü:** Parser (Tabs, Sonderzeichen, leere Ausgabe), Menüaufbau, Untermenüs ab etwa 25 Einträgen. Echt auf linuxdev (tmux 3.6, Wegwerf-Sessions): Fensterliste, direktes Anhängen an ein Fenster, Wechsel in eine andere Session, alte Session läuft weiter, keine Zeichen im laufenden Programm. Dabei entdeckt und behoben: tmux öffnet den Befehlsprompt erst nach dem `:`; ein Schreibvorgang führte zu Zeichen in der Shell, deshalb zwei Schritte mit Pause.
- **Diktat:** Stilleerkennung mit synthetischem Audio, Textbereinigung, Einfügen an der Cursorposition, Ablauf mit Attrappen (Zwischenstand, Stille, Abbruch, Fehler, nie zwei Erkennungen gleichzeitig), Modell-Download ohne Netzwerk. Echtes whisper.cpp mit Windows-Sprachausgabe: `tiny` erkennt Englisch, `small-q5_1` erkennt den deutschen Satz mit tmux und Claude Code (0,6 s, Vulkan). Ctrl+Y wird nicht an die Session weitergegeben, Einfügen und Ausführen sendet ein Enter nach einem Bracketed Paste (Playwright).
- **Shortcuts, Wiederverbindung, Autostart:** Regeln (Doppelbelegung, Ctrl+B reserviert), Browserprüfungen für alle konfigurierbaren Kürzel. Wiederverbindung: Backoff und Abbruchregeln (Core); echte Exit-Codes auf linuxdev: Normalende 0, Fehler des Befehls 3, vom Server beendete Verbindung -1 (Windows OpenSSH 9.5), unerreichbarer Host 255. Autostart: Registry-Eintrag mit eigenem Wegwerf-Schlüssel getestet.
- **Paket:** eigenständiges win-x64 mit nativen Whisper-Bibliotheken (CPU und Vulkan), 189 MB, ohne Fremdplattformen. `scripts/Install-Local.ps1` lief vollständig durch; die installierte App startet, zeigt Version 0.2.0 und legt Desktop- und Startmenü-Verknüpfung an.

Nicht automatisiert prüfbar (manuell): Mikrofon und Spracheingabe, Diktatfenster mit echtem Sprechen, Rechtsklick-Menü der tmux-Fenster per Maus, Drag-and-drop, Wiederverbindung bei echtem WLAN-Abbruch, Autostart nach Neuanmeldung.

## Version 0.1.0 (erste Implementierung)

25.10.2026, Windows-Entwicklungsrechner:

- Release-Build mit Warnings-as-errors.
- 16 lokale Konsolen-/Core-Prüfungen: SSH-Modi, Argumente, Import/Includes, Einstellungen, tmux-Abfrage/Ziel-IDs, TERM, echte Ctrl+B-Weitergabe, echte ConPTY-Größenänderung, sauberes Beenden.
- Zwei echte SSH-Prüfungen auf linuxdev: TERM=xterm-256color, stty-Größe 47 × 132, terminfo/tput und tmux verfügbar; zwei bestehende Sessions gefunden.
- Vier Browserprüfungen in Edge: Ctrl+B und Alt+2, Größenänderung und versteckte Sessions, Bracketed Paste, Remote-Ausgabe bleibt Text.
- WPF-Ladetest für Hauptfenster, Icon, Profil- und tmux-Dialog (Regression gegen fehlerhafte XAML-Event-Verdrahtung).
- WPF-Fenster und WebView2-Initialisierung am laufenden Prototyp inspiziert.

Nicht vollständig automatisiert geprüft: Drag-and-drop WPF → WebView2, alle SSH-Authentifizierungsverfahren, kompletter tmux-Maus-/Copy-Mode und der native Session-Dialogablauf. SSH-Abfrage und Zielbildung sind getestet.
