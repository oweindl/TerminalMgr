# KI-Anbieter und Nutzungsanzeige

## Einrichtung

In **Einstellungen → KI-Anbieter → KI-Anbieter einrichten** die Maschine wählen:

- **Dieser PC** nutzt die lokalen CLI-Anmeldungen des Windows-Benutzers.
- Ein **SSH-Profil** nutzt den Benutzer und die OpenSSH-Konfiguration dieses Profils. Die automatische Abfrage braucht SSH-Key/Agent; sie öffnet keinen Passwortdialog.
- **Mit ChatGPT anmelden** startet `codex login --device-auth`, **Bei Claude anmelden** startet `claude auth login`. Die Anmeldung läuft in einem separaten Konsolenfenster. Bestehende Sessions erhalten keine Befehle. Nach Abschluss **Status prüfen** wählen.
- Die CLIs müssen auf der ausgewählten Maschine installiert und im PATH verfügbar sein. Die Anwendung installiert keine CLI und kopiert keine Anmeldungen auf andere Maschinen.

### Claude-Statuszeile

**Claude-Statuszeile einrichten** installiert den mitgelieferten Collector nach `~/.terminalmgr/ai/claude-statusline.py` und trägt ihn als Statuszeilen-Befehl in die Claude-Einstellungen ein. Dafür ist Python 3.9+ erforderlich (`python` auf Windows, `python3` auf Linux).

Die bestehende Konfiguration wird gesichert. Eine vorhandene Statuszeile vom Typ `command` erhält weiterhin dieselbe Eingabe und ihre Ausgabe bleibt sichtbar. Andere Statuszeilentypen werden nicht überschrieben. **Vorherige Statuszeile wiederherstellen** entfernt den Eintrag nur, wenn er inzwischen nicht anderweitig geändert wurde.

Claude anschließend neu starten. Die Limits erscheinen erst nach einer Claude-Antwort und nur, wenn Claude sie liefert. Der Collector speichert bis zu 20 kleine Sitzungs-Snapshots mit erlaubten Metadaten; keine Prompts, Transkripte oder Tokens. Die Anzeige gleicht die gemeldete Kontoidentität mit der aktuellen Anmeldung ab.

### OpenRouter

Unter **Dieser PC** einen OpenRouter-API-Key eingeben und **Verbinden** wählen. Der Key wird über die offizielle `/api/v1/key`-Schnittstelle geprüft und erst dann im Windows Credential Manager gespeichert. Er steht weder in `settings.json` noch in Konfigurationsexporten. **Zugang entfernen** löscht die lokale Speicherung; die Widerrufung beim Anbieter erfolgt separat.

Der Zugang ist lokal und wird nicht auf SSH-Maschinen übertragen. Andere Anbieter benötigen einen eigenen Adapter; ein beliebiger API-Key liefert keine Daten über Chat-Abonnements.

## Anzeige

- Unter **KI-ABOS** erscheinen Provider, Tarif, zwei kompakte Nutzungszeilen und Reset-Zeiten. **›** öffnet ein separates Detailfenster mit allen eingelesenen sicheren Feldern und weiteren Limits.
- **⋯** bietet Aktualisieren, Wechsel zwischen ausgewähltem SSH-Server und Dieser PC sowie Einrichtung.
- Die Überschrift klappt den Bereich ein. Der Trenner darüber ändert seine Höhe. Höhe und eingeklappter Zustand werden gespeichert. Serverliste und Nutzungsbereich scrollen getrennt; die SSH-Terminalhöhe bleibt erhalten.
- Solange der Bereich sichtbar ist, wird ungefähr alle 60 Sekunden gelesen. Fehler führen zu längeren Abständen, maximal zehn Minuten. **Aktualisieren** kann sofort erneut lesen.
- Serverwechsel bricht ausstehende Abfragen ab. Verspätete Antworten werden verworfen. Alte Werte nach einem Fehler bleiben nur bei derselben Quelle erhalten und werden gekennzeichnet.
- Fehlende Werte werden nicht als 0 dargestellt. Abgelaufene Fenster zeigen **Update ausstehend**. Ältere Beobachtungen werden als veraltet markiert.

## Verfügbare Informationen und Grenzen

| Anbieter | Eingelesene Angaben |
|---|---|
| Codex | Anmeldetyp, maskiertes Konto, Tarif, mehrere Limit-Buckets, Reset-Zeiten, verfügbare Workspace-Credit-Felder und Reset-Anzahl; Token-Aktivität und Workspace-Hinweise, soweit die CLI diese optionalen Methoden unterstützt |
| Claude | Anmeldetyp, maskiertes Konto, gemeldeter Tarif/Organisation, 5h-/7d-Limits oder Gateway-Budget; Modell, Kontext, Tokenzahlen, Dauer und ausdrücklich als Schätzung beschriftete Sitzungskosten |
| OpenRouter | Key-Budget und Restbudget, Verbrauch insgesamt/heute/Woche/Monat, Budget-Zeitraum und zurückgegebene Budget-Metadaten |

Die Werte gelten für das jeweilige Konto beziehungsweise den API-Key. Nutzung über mehrere Maschinen wird nicht addiert. Codex-Limits sind keine vollständige Übersicht aller ChatGPT-Web-Modelle. Claude-Sitzungsschätzungen sind keine Abo-Rechnungen. Verlängerung, Rechnungen und Zahlungsdaten werden nicht geliefert. OpenRouter zeigt Key-Budgets, nicht automatisch das gesamte Kontoguthaben.

Erfasst wird der Standard-CLI-Kontext des gewählten Betriebssystembenutzers einschließlich geerbter Umgebung. Andere Benutzer oder zusätzliche CLI-Konfigurationsverzeichnisse werden nicht automatisch durchsucht. API-Anmeldungen werden ausdrücklich von Abonnements unterschieden. Derzeit gibt es drei Adapter; keine pauschale Anmeldung bei beliebigen Anbietern.

SSH-Abfragen verwenden einen separaten Kanal ohne PTY mit `RemoteCommand=none` und Zeitlimits. Es werden keine Modell-Turns gestartet, keine API-Käufe ausgelöst und keine Browser-Cookies oder Credential-Dateien ausgelesen. Login und Claude-Einrichtung erfolgen ausschließlich über die jeweiligen Schaltflächen.

## Prüfung

- Parser-/Transporttests: mehrere Buckets, API-Anmeldung, unbekannte Felder, abgelaufene Fenster, Kontoabgleich und keine Secrets im Anzeigemodell.
- WPF-Tests: zwei Provider unter 190 px, Details/Setup, einklappbarer Bereich und verspätete Antwort nach Serverwechsel.
- Python-Tests: Erhaltung/Wiederherstellung der Statuszeile, Schutz vor Überschreiben späterer Änderungen, Eingabeweitergabe und Snapshot-Allowlist.
- Lokaler Smoke-Test liest nur Metadaten; die Ausgabe enthält lediglich Provider, Feld-/Fensteranzahlen und Erfolg, keine Kontodaten.

Offizielle Schnittstellen: [Codex app-server](https://developers.openai.com/codex/app-server/), [Claude CLI](https://code.claude.com/docs/en/cli-reference), [Claude-Statuszeile](https://code.claude.com/docs/en/statusline), [OpenRouter Limits](https://openrouter.ai/docs/api/reference/limits).
