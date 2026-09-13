# Aufgabenplanung

Stand: 13. September 2026

Diese Datei fasst die geplanten Arbeiten und ihre Abhängigkeiten zusammen. Die
GitHub-Issues bleiben die verbindliche Quelle für Anforderungen und Diskussionen.
Technische Zwischenstände und Übergaben werden weiterhin in `HANDOFF.md`
dokumentiert.

## Übersicht

| Priorität | Thema | Status | Abhängigkeiten |
|---:|---|---|---|
| 1 | [#11 Virtueller Hotspot und Funkgerät für Tests](https://github.com/Nikoshi/DMRoute-ng/issues/11) | Implementiert auf `feat/issue-11-virtual-hotspot`; PR ausstehend | keine |
| 2 | [#9 Check Device](https://github.com/Nikoshi/DMRoute-ng/issues/9) | Protokollanalyse erforderlich | Hardware-Captures; anschließend #11 für reproduzierbare Tests |
| 3 | [#10 SDS an Funkgerät senden](https://github.com/Nikoshi/DMRoute-ng/issues/10) | Schnittstelle und Protokollablauf offen | #11; DMR-Datenencoder |
| 4 | [#14 CLI/TUI für den virtuellen DMR-Testteilnehmer](https://github.com/Nikoshi/DMRoute-ng/issues/14) | Planung erforderlich | #11 |
| 5 | [#15 Semantische DMR-Szenarien im Emulator](https://github.com/Nikoshi/DMRoute-ng/issues/15) | Protokollanalyse erforderlich | #11, #9 und #10 |

## 1. Virtueller Hotspot und Funkgerät für Tests (#11)

### Ziel

Ein Testteilnehmer soll sich gegenüber `DmrServer` wie ein echter
Homebrew-Hotspot verhalten und definierte DMRD-Paketfolgen senden sowie Antworten
des Masters prüfen können. Er bildet die Grundlage für reproduzierbare Tests von
Radio Check und ausgehenden SDS.

### Umgesetzter Umfang

- `DMRoute_ng.Emulator` ist eine eigenständige Core-Bibliothek ohne Referenz auf
  den Server oder xUnit.
- Anmeldung mit `RPTL`, Challenge, `RPTK` und `RPTC`, Keepalive mit
  `RPTPING`/`MSTPONG`, `RPTCL` und `MSTNAK` sind umgesetzt.
- `VirtualRadio` spielt zeitgesteuerte, aufgezeichnete DMRD-Szenarien ein;
  `VirtualHotspot` zeichnet weitergeleitete Frames in einem begrenzten Puffer auf.
- Loopback-Tests decken zwei Hotspots, Routing ohne Gruppenecho, falschen PSK,
  fremde Zonen-ID, Timeout, Soft-Reconnect und Neuanmeldung ab.

### Nächster Schritt

- Branch `feat/issue-11-virtual-hotspot` reviewen und als PR für #11 einreichen.
- Nach dem Merge #9 und #10 auf die neue Szenario-API ausrichten.

### Randbedingungen

- Der Emulator liegt im Testprojekt oder in einem separaten Testwerkzeug und wird
  nicht Bestandteil des produktiven NativeAOT-Binaries.
- Tests verwenden Loopback-Sockets und feste Zeitgrenzen.
- Protokollbytes werden mit fokussierten Writer- und Parser-Tests abgesichert.

## 2. Check Device (#9)

### Ziel

Der Master soll Radio-Check-Anfragen korrekt erkennen und beantworten können.
Aktives Prüfen eines Teilnehmers vor Ablauf eines Roaming-Eintrags wird erst nach
Klärung des grundlegenden Protokollablaufs bewertet.

### Nächster Schritt

- Radio Checks mit AnyTone und Retevis in beide Richtungen mitschneiden.
- CSBK-/DMRD-Felder, Quell- und Zieladressierung, Antwortpakete, Wiederholungen
  und Timeouts dokumentieren.
- Prüfen, welcher Anteil bereits transparent durch `MicroSubnetRouter` geroutet
  wird und welche semantische Verarbeitung fehlt.
- Danach Issue #9 mit eindeutigem Verhalten und Hardware-Abnahmetest ergänzen.

### Randbedingungen

- Keine Implementierung aus vermuteten Paketformaten; der Hardware-Mitschnitt ist
  Voraussetzung.
- Neue Verarbeitung im DMR-Empfangspfad muss innerhalb vorkonfigurierter
  Kapazitäten ohne verwaltete Allokationen arbeiten.
- Paketwriter verwenden caller-eigene `Span<byte>`-Puffer und bleiben
  NativeAOT-kompatibel.

## 3. SDS an Funkgerät senden (#10)

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

## 4. CLI/TUI für den virtuellen DMR-Testteilnehmer (#14)

### Ziel

Die programmatische API aus #11 soll später interaktiv bedient werden können.
Das Frontend konfiguriert Verbindungen, zeigt den Sitzungszustand und spielt
vorhandene Szenarien ab, ohne den Emulator an den Server zu koppeln.

### Nächster Schritt

- Bedienmodell, sichere PSK-Behandlung, Ausgabeformat und Abnahmekriterien im
  Issue festlegen.
- Entscheiden, ob zuerst eine skriptbare CLI oder direkt eine TUI entsteht.

## 5. Semantische DMR-Szenarien im Emulator (#15)

### Ziel

Nach den Protokollanalysen aus #9 und #10 soll der Emulator Radio Check, Sprache
und SDS aus semantischen Eingaben erzeugen können, statt nur Captures abzuspielen.

### Nächster Schritt

- Benötigte Writer und Zustandsabläufe aus den bestätigten Hardware-Captures
  ableiten.
- Eine serverunabhängige Projektgrenze für gemeinsam nutzbare Protokollbausteine
  festlegen.

## Spätere Hardware-Validierung

- Gruppen-SDS mit mindestens zwei angemeldeten Hotspots prüfen. Der vorhandene
  Mitschnitt bestätigt die korrekte Group-Call-Markierung, belegt aber noch keine
  Verteilung an einen zweiten Hotspot.
- Confirmed SDS vom Retevis-Gerät erneut untersuchen. Beim bisherigen Versuch
  folgten auf den bestätigten Datenheader keine Rate-3/4-Datenblöcke.

## Abgeschlossen

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
