# Makyx-Leitstand

TerminalMgr zeigt Läufe und Einzelaufträge aus **Makyx** (Software-Fabrik) in einer Leiste **LÄUFE** unter KI-ABOS, meldet per Toast, wenn etwas neu auf dich wartet, und springt auf Klick in die Makyx-Seite oder in das tmux-Fenster der Instanz. Es liest nur; das Token hat Leserechte.

## Einrichten
**Mit Kopieren und Einfügen (empfohlen):**
1. In Makyx › Einstellungen › **Leitstand-Zugänge** einen Zugang anlegen und im Block „Konfiguration für TerminalMgr“ den Text **kopieren**.
2. In TerminalMgr › Einstellungen › **Makyx-Leitstand** den Text in das Feld **Konfiguration einlesen** einfügen (liegt er schon in der Zwischenablage, steht er beim Öffnen schon da) und **Einlesen** klicken. Alternativ **Aus Datei …** mit der in Makyx heruntergeladenen Datei.
3. Die **Vorschau** zeigt Makyx-Name, Adressen, Zugang, den **CA-Fingerprint** (bitte mit Makyx vergleichen) und die Art (Einmal-Code oder Token). **Übernehmen** löst den Einmal-Code bei Makyx gegen das Token ein, legt das Token im Windows-Anmeldeinformationsspeicher ab, speichert Adresse und CA und prüft die Verbindung. Bei Erfolg erscheint die Leiste LÄUFE. Gibt es schon eine Verbindung, fragt TerminalMgr vorher „Bestehenden Leitstand ersetzen?“.
4. Der Einmal-Code ist 10 Minuten gültig und nur einmal einlösbar. Meldungen wie „Der Code ist abgelaufen“ oder „schon benutzt“ heißen: in Makyx unter Leitstand-Zugänge die Konfiguration neu erzeugen. Steht ausnahmsweise das Token selbst im Text, bietet TerminalMgr danach **Zwischenablage leeren** an. Die Clipboard-Liste zeigt solche Einträge nur als „Makyx-Leitstand-Konfiguration“.

**Manuell** (unter „Manuell einrichten“, eingeklappt): Schalter an, Adresse (`https://makyx.example:8801`), CA-Fingerprint, Token eintragen, **Verbindung prüfen**, Speichern.

Das Token liegt immer im Windows-Anmeldeinformationsspeicher (Ziel `TerminalMgr/Makyx/Leitstand`), nie in `settings.json`, nie im Export und nie im Klartext in der Clipboard-Liste.

## Bedienung
- Leiste LÄUFE (›/⌄): Wartendes zuerst, dann Arbeitendes, dann zuletzt Beendetes. Klick öffnet die Makyx-Seite im Browser.
- Rechtsklick: **In Makyx öffnen**, **Im Terminal öffnen** (bei einer Instanz in einem tmux-Fenster und einem SSH-Profil, das zum Rechner passt: gleicher Name oder gleiches Ziel), **Link kopieren**. Strukturierte Sessions haben kein Terminal.
- **Im Terminal öffnen** verbindet mit dem Profil des Rechners und hängt sich per `tmux -L <Socket> attach-session -t <Session>:<Fenster>` an. Der Eintrag „Makyx · <Fenster>“ erscheint in der Serverliste und wird nicht gespeichert.
- Toast „Wartet auf dich“ bei neuem Wartenden (abschaltbar); beim Start keine Meldungen für Bestehendes.
- Befehlspalette (Ctrl+Shift+K): Gruppe **Makyx** mit den laufenden Aufträgen.

## Sessions und tmux (Notiz 16, ab 09.11.2026)
- **LÄUFE nach Projekt:** je Projekt eine Zeile mit Zähler und **+** (neue Session), darunter die aktiven Sessions als „kurzname · Rolle“ mit Status und Alt-Nummer ihres Tabs; Klick öffnet das Terminal. Darunter **ZULETZT** mit den beendeten Läufen.
- **Neue Claude-Session** (Ctrl+N, „+ Session“, „+“ am Projekt): Projekt mit Suche, Name, Art (interaktiv oder Lauf), Ziel-Host mit freiem RAM, Arbeitsordner (Worktree oder Hauptordner), Agent und Modell, erste Nachricht.
- **Namen:** Tab und Eintrag heißen „Projekt · kurzname“. Ohne `kurzname` von Makyx gilt der Name des tmux-Fensters, sonst die Kennung.
- **Eigene Ansicht je Tab:** Ein Makyx-Tab hängt über eine eigene gruppierte Session (`tm-xxxxxxxx`, `destroy-unattached`) an der Makyx-Session. Fensterwechsel bewegen nie einen anderen Tab.
- **Fenster wechseln** (Ctrl+Shift+W, Kontextmenü, Kontextleiste): alle Fenster des Servers, normaler tmux und Makyx-Socket, gruppiert; Ansichts-Sessions ausgeblendet. Enter hier, Ctrl+Enter neuer Tab, N neues Fenster, R umbenennen (Makyx-Sessions über `PATCH sessions/{id}`).
- **Kontextleiste** über dem Terminal eines Makyx-Tabs: Projekt, Session, Rechner, tmux-Ziel, Status; „Auftrag senden“ beantwortet, was für die Session wartet.
- **Icon „Läufe“** in der linken Leiste (Ctrl+Shift+L) mit Zähler der Wartenden.

## Technik
Abfrage von `/laeufe`, `/wartet` beim Start, danach der Ereignisstrom `/ereignisse` (Server-Sent Events); bei Abbruch Wiederverbindung nach 2/4/8/16/30 s und Nachholen mit `seit`. Die CA des Runner-Kanals wird über ihren SHA-256-Fingerprint festgelegt; der Servername wird weiter geprüft. Schnittstelle: `docs/leitstand-api.yaml` im Makyx-Repository.
Tests: `tests/TerminalMgr.Tests/MakyxTests.cs` und `MakyxClientTests.cs` (Konsolenprüfungen, nur neutrale Namen), `TestMakyxDashboard` in den UI-Prüfungen.
