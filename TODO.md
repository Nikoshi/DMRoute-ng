# Aufgabenplanung

Stand: 13. September 2026

Diese Datei fasst die geplanten Arbeiten und ihre Abhängigkeiten zusammen. Die
GitHub-Issues bleiben die verbindliche Quelle für Anforderungen und Diskussionen.
Technische Zwischenstände und Übergaben werden weiterhin in `HANDOFF.md`
dokumentiert.

## Übersicht

| Priorität | Thema | Status | Abhängigkeiten |
|---:|---|---|---|
| 1 | [#9 Check Device](https://github.com/Nikoshi/DMRoute-ng/issues/9) | Capture-Plan erstellt; Hardware-Lauf als nächster Schritt | #11 erfüllt; AnyTone und Retevis verfügbar |
| 2 | [#10 SDS an Funkgerät senden](https://github.com/Nikoshi/DMRoute-ng/issues/10) | Danach ausarbeiten | #11 erfüllt; DMR-Datenencoder |
| 3 | [#15 Semantische DMR-Szenarien im Emulator](https://github.com/Nikoshi/DMRoute-ng/issues/15) | Nach den Protokollarbeiten | #11 erfüllt; #9 und #10 |
| 4 | [#21 Masterinitiierter Radio Check](https://github.com/Nikoshi/DMRoute-ng/issues/21) | Folgeticket; nach passiver Erkennung und Writer | #9 und #15 |
| 5 | [#16 Testorchestrator für mehrere Master](https://github.com/Nikoshi/DMRoute-ng/issues/16) | Planung erforderlich | #11 erfüllt; nach #15 |
| 6 | [#14 CLI/TUI für den virtuellen DMR-Testteilnehmer](https://github.com/Nikoshi/DMRoute-ng/issues/14) | Planung erforderlich | #11 erfüllt; stabile Szenario-API aus #15 |
| 7 | [#17 Virtuelles Funkgerät kommuniziert mit echtem Funkgerät](https://github.com/Nikoshi/DMRoute-ng/issues/17) | Planung erforderlich | #15; Hardware-/RF-Konzept offen |

## 1. Check Device (#9)

### Ziel

Der Master soll Radio-Check-Anfragen und -Antworten korrekt erkennen,
unverändert routen und den zusammengefassten Vorgang in Log und MQTT melden.
Das Ergebnis enthält die Anzahl der Versuche und Funkwiederholungen sowie die
Dauer. Aktives Prüfen durch den Master ist in #21 ausgegliedert.

### Nächster Schritt

- Den deutschen
  [Radio-Check-Capture-Plan](docs/radio-check-capture-plan.adoc) mit je drei
  erfolgreichen und unbeantworteten Prüfungen pro Richtung durchführen.
- CSBK-/DMRD-Felder, Quell- und Zieladressierung, Antwortpakete, Wiederholungen
  und Timeouts dokumentieren.
- Echte Funkwiederholungen von Master-Rückleitungen unterscheiden und daraus
  `attempts`, `retries` und die Vorgangsdauer bestimmen.
- Danach Issue #9 um die bestätigten Paketfelder und Implementierungsabnahme
  ergänzen und den WIP-Status entfernen.

### Randbedingungen

- Keine Implementierung aus vermuteten Paketformaten; der Hardware-Mitschnitt ist
  Voraussetzung.
- Neue Verarbeitung im DMR-Empfangspfad muss innerhalb vorkonfigurierter
  Kapazitäten ohne verwaltete Allokationen arbeiten.
- Paketwriter verwenden caller-eigene `Span<byte>`-Puffer und bleiben
  NativeAOT-kompatibel.

## 2. SDS an Funkgerät senden (#10)

### Ziel

Eine externe Anforderung soll eine private SDS vom Master zu einem erreichbaren
Funkgerät auslösen können.

### Offene Produktentscheidung

Vor der Implementierung muss die externe Schnittstelle festgelegt werden. Zu
bewerten sind mindestens MQTT und HTTP hinsichtlich Authentifizierung,
Rückmeldung, Betriebsaufwand und NativeAOT-Unterstützung.

### Nächster Schritt

- Aus den vorhandenen Hardware-Captures den vollständigen bestätigten
  SDS-Dialog ableiten.
- TMS-, UDP-, IPv4-, DMR-Datenheader-, BPTC- und Trellis-Writer sowie
  Blocksequenzierung spezifizieren.
- Zustellung, Bestätigung, Timeout, Wiederholung, Offline-Ziel und parallele
  Nachrichten als Zustandsmodell festlegen.
- Issue #10 anschließend um API, Fehlerantworten, Grenzen und Abnahmekriterien
  ergänzen.

### Randbedingungen

- Der virtuelle Hotspot aus #11 dient als automatisierter Empfänger und
  Protokollprüfer.
- Versandzustand ist begrenzt und wird beim Start vorab reserviert.
- Writer erhalten Zielpuffer vom Aufrufer; variable Paketarrays, Reflection und
  dynamische Codeerzeugung sind im produktiven Pfad ausgeschlossen.

## 3. Semantische DMR-Szenarien im Emulator (#15)

### Ziel

Nach den Protokollanalysen aus #9 und #10 soll der Emulator Radio Check, Sprache
und SDS aus semantischen Eingaben erzeugen können, statt nur Captures abzuspielen.

### Nächster Schritt

- Benötigte Writer und Zustandsabläufe aus den bestätigten Hardware-Captures
  ableiten.
- Eine serverunabhängige Projektgrenze für gemeinsam nutzbare Protokollbausteine
  festlegen.

## 4. Masterinitiierter Radio Check (#21)

### Ziel

Der Master soll einen Radio Check selbst auslösen und Antwort, Timeout,
Wiederholungszahl und Dauer für interne Abläufe bereitstellen. Ein späterer
Anwendungsfall ist die Erreichbarkeitsprüfung vor Ablauf eines Roaming-Eintrags.

### Nächster Schritt

- Nach #9 und #15 Identität des Masters, internen beziehungsweise externen
  Auslöser, begrenzten Versandzustand und Rate-Limit festlegen.
- Verhalten für lokale Teilnehmer, Gäste und über Mesh bekannte Teilnehmer
  spezifizieren.

## 5. Testorchestrator für mehrere Master (#16)

### Ziel

Ein unabhängiges Testwerkzeug soll beliebig viele DMRoute-ng-Master starten,
miteinander verbinden und gemeinsam mit den Teilnehmern aus #11 steuern können.

### Nächster Schritt

- Prozessmodell, dynamische Ports, Topologiekonfiguration sowie Start-,
  Bereitschafts- und Stoppverhalten festlegen.
- Beobachtbare Zustände, Fehlerfälle und Abnahmekriterien im Issue ergänzen.

### Randbedingungen

- Der Orchestrator bleibt in einem eigenen Projekt vom Server unabhängig.
- Die virtuellen Hotspots und Funkgeräte aus #11 bilden die Teilnehmerseite der
  späteren Mehrmaster-Szenarien.

## 6. CLI/TUI für den virtuellen DMR-Testteilnehmer (#14)

### Ziel

Die programmatische API aus #11 soll später interaktiv bedient werden können.
Das Frontend konfiguriert Verbindungen, zeigt den Sitzungszustand und spielt
vorhandene Szenarien ab, ohne den Emulator an den Server zu koppeln.

### Nächster Schritt

- Nach Stabilisierung der semantischen Szenario-API Bedienmodell, sichere
  PSK-Behandlung, Ausgabeformat und Abnahmekriterien im Issue festlegen.
- Entscheiden, ob zuerst eine skriptbare CLI oder direkt eine TUI entsteht.

## 7. Virtuelles Funkgerät kommuniziert mit echtem Funkgerät (#17)

### Ziel

Der virtuelle Teilnehmer aus #11 soll später Tests mit einem echten Funkgerät
ermöglichen, beispielsweise den Versand einer SDS an das reale Gerät.

### Nächster Schritt

- RF-/Hardware-Grenze, Nachrichtenrichtung, unterstützte Geräte und benötigte
  Protokollbausteine klären.
- Abhängigkeiten, Sicherheitsgrenzen und Abnahmekriterien anschließend im Issue
  festlegen.

### Randbedingungen

- #11 stellt die bestätigte Transportgrundlage bereit; die semantische
  Szenario-API aus #15 wird für die Funkaktionen benötigt.
- Vor einer Implementierung ist ein Hardware- und Protokollkonzept erforderlich.

## Spätere Hardware-Validierung

- Gruppen-SDS mit mindestens zwei angemeldeten Hotspots prüfen. Der vorhandene
  Mitschnitt bestätigt die korrekte Group-Call-Markierung, belegt aber noch keine
  Verteilung an einen zweiten Hotspot.
- Confirmed SDS vom Retevis-Gerät erneut untersuchen. Beim bisherigen Versuch
  folgten auf den bestätigten Datenheader keine Rate-3/4-Datenblöcke.

## Abgeschlossen

- [#11 Virtueller Homebrew-Hotspot und Funkgerät für Tests](https://github.com/Nikoshi/DMRoute-ng/issues/11),
  umgesetzt mit [PR #18](https://github.com/Nikoshi/DMRoute-ng/pull/18): am
  13. September 2026 als `b849ae0` gemergt und durch den PR geschlossen.
- [#8 Config-Wizard](https://github.com/Nikoshi/DMRoute-ng/issues/8), umgesetzt
  mit [PR #13](https://github.com/Nikoshi/DMRoute-ng/pull/13): gemergt und durch
  den PR geschlossen.

## Pflege

- Bei Beginn eines Arbeitspakets Status, Branch und nächsten konkreten Schritt
  aktualisieren.
- Nach neuen Protokoll- oder Hardware-Erkenntnissen Abhängigkeiten und offene
  Entscheidungen nachführen.
- Nach einem PR-Merge den Eintrag nach „Abgeschlossen“ verschieben und die
  Reihenfolge der verbleibenden Aufgaben neu bewerten.
- GitHub-Issue, `TODO.md` und `HANDOFF.md` dürfen sich beim Status eines
  Arbeitspakets nicht widersprechen.
