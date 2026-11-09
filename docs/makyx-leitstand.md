# Makyx-Leitstand

TerminalMgr zeigt Läufe und Einzelaufträge aus **Makyx** (Software-Fabrik) in einer Leiste **LÄUFE** unter KI-ABOS, meldet per Toast, wenn etwas neu auf dich wartet, und springt auf Klick in die Makyx-Seite oder in das tmux-Fenster der Instanz. Es liest nur; das Token hat Leserechte.

## Einrichten
1. In Makyx › Einstellungen › **Leitstand-Zugänge** einen Zugang anlegen. Der Dialog zeigt das Token (nur einmal), die Adresse des Runner-Kanals und den **CA-Fingerprint**.
2. In TerminalMgr › Einstellungen › **Makyx-Leitstand**: Schalter an, Adresse (`https://makyx.example:8801`), CA-Fingerprint, Token eintragen, **Verbindung prüfen**, Speichern.
   Das Token liegt im Windows-Anmeldeinformationsspeicher (Ziel `TerminalMgr/Makyx/Leitstand`), nie in `settings.json` und nie im Export.

## Bedienung
- Leiste LÄUFE (›/⌄): Wartendes zuerst, dann Arbeitendes, dann zuletzt Beendetes. Klick öffnet die Makyx-Seite im Browser.
- Rechtsklick: **In Makyx öffnen**, **Im Terminal öffnen** (bei einer Instanz in einem tmux-Fenster und einem SSH-Profil, das zum Rechner passt: gleicher Name oder gleiches Ziel), **Link kopieren**. Strukturierte Sessions haben kein Terminal.
- **Im Terminal öffnen** verbindet mit dem Profil des Rechners und hängt sich per `tmux -L <Socket> attach-session -t <Session>:<Fenster>` an. Der Eintrag „Makyx · <Fenster>“ erscheint in der Serverliste und wird nicht gespeichert.
- Toast „Wartet auf dich“ bei neuem Wartenden (abschaltbar); beim Start keine Meldungen für Bestehendes.
- Befehlspalette (Ctrl+Shift+K): Gruppe **Makyx** mit den laufenden Aufträgen.

## Technik
Abfrage von `/laeufe`, `/wartet` beim Start, danach der Ereignisstrom `/ereignisse` (Server-Sent Events); bei Abbruch Wiederverbindung nach 2/4/8/16/30 s und Nachholen mit `seit`. Die CA des Runner-Kanals wird über ihren SHA-256-Fingerprint festgelegt; der Servername wird weiter geprüft. Schnittstelle: `docs/leitstand-api.yaml` im Makyx-Repository.
Tests: `tests/TerminalMgr.Tests/MakyxTests.cs` und `MakyxClientTests.cs` (Konsolenprüfungen, nur neutrale Namen), `TestMakyxDashboard` in den UI-Prüfungen.
