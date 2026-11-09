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
- Baum MAKYX (›/⌄): Rechner (Punkt grün = verbunden, „n aktiv“ = belegte Session-Plätze) › Projekt › laufende Instanz. Klick auf Rechner oder Projekt klappt ein und aus, Klick auf eine Instanz öffnet ihr Terminal (ohne Terminal die Makyx-Seite). Rechtsklick: Rechner – Shell öffnen; Projekt – Neue Session, Lauf starten, Projektseite; Instanz – Terminal, Antworten, Makyx, Browser, Link, Anhalten. Fertige Läufe zeigt nur Makyx.
- Rechtsklick: **In Makyx öffnen**, **Im Terminal öffnen** (bei einer Instanz in einem tmux-Fenster und einem SSH-Profil, das zum Rechner passt: gleicher Name oder gleiches Ziel), **Link kopieren**. Strukturierte Sessions haben kein Terminal.
- **Im Terminal öffnen** verbindet mit dem Profil des Rechners und hängt sich per `tmux -L <Socket> attach-session -t <Session>:<Fenster>` an. Der Eintrag „Makyx · <Fenster>“ erscheint in der Serverliste und wird nicht gespeichert.
- Toast „Wartet auf dich“ bei neuem Wartenden (abschaltbar); beim Start keine Meldungen für Bestehendes.
- Befehlspalette (Ctrl+Shift+K): Gruppe **Makyx** mit den laufenden Aufträgen.

## Technik
Abfrage von `/laeufe`, `/wartet` beim Start, danach der Ereignisstrom `/ereignisse` (Server-Sent Events); bei Abbruch Wiederverbindung nach 2/4/8/16/30 s und Nachholen mit `seit`. Die CA des Runner-Kanals wird über ihren SHA-256-Fingerprint festgelegt; der Servername wird weiter geprüft. Schnittstelle: `docs/leitstand-api.yaml` im Makyx-Repository.
Tests: `tests/TerminalMgr.Tests/MakyxTests.cs` und `MakyxClientTests.cs` (Konsolenprüfungen, nur neutrale Namen), `TestMakyxDashboard` in den UI-Prüfungen.
