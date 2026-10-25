# Validierung

25.10.2026, Windows-Entwicklungsrechner:

- Release-Build mit Warnings-as-errors.
- 16 lokale Konsolen-/Core-Prüfungen: SSH-Modi, Argumente, Import/Includes, Einstellungen, tmux-Abfrage/Ziel-IDs, TERM, echte Ctrl+B-Weitergabe, echte ConPTY-Größenänderung, sauberes Beenden.
- Zwei echte SSH-Prüfungen auf linuxdev: TERM=xterm-256color, stty-Größe 47 × 132, terminfo/tput und tmux verfügbar; zwei bestehende Sessions gefunden.
- Vier Browserprüfungen in Edge: Ctrl+B und Alt+2, Größenänderung und versteckte Sessions, Bracketed Paste, Remote-Ausgabe bleibt Text.
- WPF-Fenster und WebView2-Initialisierung am laufenden Prototyp inspiziert.

Nicht vollständig automatisiert geprüft: Drag-and-drop WPF → WebView2, alle SSH-Authentifizierungsverfahren, kompletter tmux-Maus-/Copy-Mode und der native Session-Dialogablauf. SSH-Abfrage und Zielbildung sind getestet.
