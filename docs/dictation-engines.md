# Diktat: drei Engines hinter einer Schnittstelle

Alle Engines implementieren `ISpeechEngine` (Windows-Projekt): `Start`, `Stop`, `Cancel`, Zustand, Eingangspegel, Zwischenstand als
*bestätigte* und *vorläufige* Wörter, Endtext, Fehler und `Metrics` (Zeiten für den Vergleich). Die Wahl steht in den Einstellungen unter
„Diktat“ (`Engine`); vorgegeben ist Whisper. Keine Engine braucht eine Azure-Ressource oder einen Schlüssel.

| Engine | Klasse | Läuft hier? |
|---|---|---|
| Whisper (lokal, GPU) | `DictationSession` + `WhisperTranscriber` | ja, vollständig |
| Web Speech API in WebView2 | `WebSpeechEngine` + `Terminal/terminal.js` | **nein**, Fehler `network` |
| `Windows.Media.SpeechRecognition` | `WindowsSpeechEngine` | **nur mit Windows-Einstellung „Onlinespracherkennung“**, die TerminalMgr nicht ändert |

## Prüfung, ob die beiden anderen Engines in der App überhaupt laufen

Gemessen am 2026-10-25 auf Windows 11 (26100), WebView2-Laufzeit 155.0.4283.62, deutsche Voreinstellungen.

**Web Speech API.** Ein WebView2 mit derselben Konfiguration wie das Terminalfenster (sicherer Ursprung über das virtuelle Host-Mapping,
Mikrofon-Berechtigung erlaubt) kennt `webkitSpeechRecognition`, `onstart` und `onaudiostart` kommen, danach sofort
`onerror: network` und `onend` (nach rund 0,6 s). Der Spracherkennungsdienst des Browsers ist im eingebetteten WebView2 nicht erreichbar;
es gibt keinen Schlüssel, den man eintragen könnte. Die Engine ist vollständig angebunden (Playwright-Test mit einem Ersatz-Erkenner
prüft das Protokoll), meldet den Fehler aber als „nicht verfügbar“. Das Mikrofon darf die Seite nur erlauben, solange diese Engine aufnimmt.

**Windows.Media.SpeechRecognition.** Die Sprache de-DE lässt sich einrichten (`CompileConstraintsAsync`: Success), das Starten der
fortlaufenden Erkennung scheitert aber mit `0x80045509` („The speech privacy policy was not accepted“). Windows lässt die Erkennung nur zu, wenn unter
Einstellungen › Datenschutz › Spracherkennung „Onlinespracherkennung“ eingeschaltet ist; dann geht das Audio an den Microsoft-Dienst, es ist also
nicht lokal. Die Engine meldet das mit Hinweis; sie ändert die Einstellung nicht. Sobald die Einstellung an ist, läuft sie mit
Zwischenstand (`HypothesisGenerated`) und Endtext (`ResultGenerated`); das wurde hier **nicht** gemessen. Prüfen mit
`dotnet run --project tests/TerminalMgr.Tests -c Release -- --engine-smoke`.

Folge: Vergleichbare Zahlen gibt es nur für Whisper. Für die beiden anderen Engines gibt es die Messung erst, wenn die Voraussetzung
geschaffen ist (Windows-Einstellung bzw. ein WebView2 mit Dienst).

## Whisper: beschleunigte Zwischenergebnisse

* Zwischenstand: kleines Modell (`small-q5_1`) über Vulkan auf der Grafikkarte, nur die **letzten bis zu 7 s** Audio (Einstellung
  `InterimWindowSeconds`), der Zyklus läuft etwa alle **0,7 s** (`InterimCycleSeconds`). Das Fenster beginnt 0,2 s vor dem letzten bestätigten Wort.
* Reduzierter Audiokontext (`audio_ctx`): 50 Positionen je Sekunde plus Reserve, auf Vielfache von 64 gerundet, mindestens 256. Kleinere, krumme Werte
  ließen das Vulkan-Backend hängen.
* Wortweise Ergebnisse mit Zeitstempeln (Segmentlänge 1, Trennung an Wörtern, Token-Zeitstempel), keine Wiederholung bei höheren Temperaturen.
* `WordAgreement` (Core): ein Wort gilt als **bestätigt**, wenn zwei aufeinanderfolgende Ergebnisse es übereinstimmend liefern. Bestätigte
  Wörter werden nicht mehr neu berechnet und ändern sich nie; der Rest ist vorläufig und erscheint kursiv.
* Endtext: das ganze Segment mit dem eingestellten Modell (Vorgabe `large-v3-turbo-q5_0`).
* Ein erstes Fenster braucht mindestens 1 s Audio (kürzere werden von whisper aufgefüllt und füllen sich mit erfundenen Wörtern).
* Alle Erkennungsläufe laufen im Prozess nacheinander. Zwei whisper-Kontexte gleichzeitig auf derselben GPU (kleines und großes Modell) ließen das
  Vulkan-Backend hängen und stürzten ab. Ein abbrechbares Token wird nicht an Whisper.net gegeben (Zugriffsverletzung im nativen Abbruch-Callback);
  stattdessen wird zwischen den Wörtern geprüft.
* Beim Öffnen des Diktats werden beide Modelle geladen und die GPU-Kernel einmal übersetzt, damit die ersten Wörter nicht warten.

## Messung (Messmodus)

`dotnet run --project tests/TerminalMgr.Tests -c Release -- --dictation-bench` spielt synthetisierte deutsche Sprache (Windows-Stimme Hedda,
Fachbegriffe wie tmux, Claude Code, GitHub, Docker) in Echtzeit in das Diktat und vergleicht das neue Verfahren mit dem alten (ganzes Segment alle
1,75 s mit dem großen Modell). `TERMINALMGR_BENCH_TRACE=1` zeigt jeden Erkennungsaufruf. In der App schreibt `TERMINALMGR_SPEECH_METRICS=1`
die Zeiten jeder Runde in die Statuszeile und in `speech-metrics.log` im Datenordner.

Rechner: Intel Core i7-12700F, NVIDIA GeForce RTX 3060 Ti (Vulkan), `small-q5_1` für den Zwischenstand, `large-v3-turbo-q5_0` für den Endtext.

| Engine | Probe | Audio | erstes Zwischenergebnis | Zyklen | Zyklus Ø / max | Endtext nach Ende | WER Zwischenstand | WER Endtext | geänderte bestätigte Wörter |
|---|---|---|---|---|---|---|---|---|---|
| Whisper Streaming (small-q5_1, Fenster) | kurz | 4,1 s | 963 ms | 6 | 233 / 320 ms | 1610 ms | 36 % | 18 % | 0 |
| Whisper alt (Gesamtsegment, large-v3-turbo-q5_0) | kurz | 4,1 s | 2168 ms | 2 | 479 / 566 ms | 718 ms | 27 % | 18 % | 0 |
| Whisper Streaming (small-q5_1, Fenster) | mittel | 8,8 s | 955 ms | 12 | 253 / 357 ms | 642 ms | 18 % | 5 % | 0 |
| Whisper alt (Gesamtsegment, large-v3-turbo-q5_0) | mittel | 8,8 s | 2180 ms | 5 | 453 / 580 ms | 528 ms | 5 % | 5 % | 0 |
| Whisper Streaming (small-q5_1, Fenster) | lang | 20,2 s | 1006 ms | 29 | 262 / 409 ms | 817 ms | 26 % | 13 % | 0 |
| Whisper alt (Gesamtsegment, large-v3-turbo-q5_0) | lang | 20,2 s | 2162 ms | 11 | 542 / 715 ms | 681 ms | 13 % | 13 % | 0 |
| Web Speech API (WebView2) | – | – | nicht messbar: `network` | | | | | | |
| Windows-Spracherkennung | – | – | nicht messbar: Datenschutzeinstellung aus (`0x80045509`) | | | | | | |

Lesart:

* Das erste Wort erscheint nach etwa 1 s statt nach 2,2 s; ein Zyklus dauert gut 0,25 s statt 0,45 bis 0,55 s und bleibt bei langen
  Diktaten gleich (die Fenstergröße ist begrenzt), während die alte Variante mit der Länge wächst.
* Der Zwischenstand des kleinen Modells ist ungenauer als der des großen (WER 18 bis 36 % gegen 5 bis 27 %). Dafür ist der Endtext nicht schlechter, er kommt
  weiter vom großen Modell. Bestätigte Wörter änderten sich in keiner Probe nachträglich.
* „Endtext nach Ende“ ist die Zeit vom Ende der Aufnahme (nach der Stille) bis zum Endtext; der Wert von 1610 ms in der ersten Zeile entstand, weil
  noch ein Zwischenlauf lief, auf den der Endlauf warten musste.
* Die Audiodaten sind synthetisch (saubere Stimme, kein Raum, kein Rauschen). Mit echtem Mikrofon fallen die Fehlerraten höher aus; die Zeiten
  bleiben gleich.
