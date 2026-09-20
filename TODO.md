# Aufgabenplanung

Stand: 20. September 2026

Diese Datei fasst die geplanten Arbeiten und ihre Abhängigkeiten zusammen. Die
GitHub-Issues bleiben die verbindliche Quelle für Anforderungen und Diskussionen.
Technische Zwischenstände und Übergaben werden in `HANDOFF.md` dokumentiert.

## Übersicht

| Priorität | Thema | Status | Abhängigkeiten |
|---:|---|---|---|
| 1 | [#10 SDS-Echo-Dienst](https://github.com/Nikoshi/DMRoute-ng/issues/10) | Encoder und Echo-Checkpoint implementiert; Hardwareabnahme offen | funktionierender Hotspot; AnyTone und Retevis |
| 2 | [#9 Radio Check](https://github.com/Nikoshi/DMRoute-ng/issues/9) | Zurückgestellt bis zur Reparatur des zweiten Hotspots | zwei getrennte RF-Endpunkte |
| 3 | [#24 Authentifizierte SDS-Sendeschnittstelle](https://github.com/Nikoshi/DMRoute-ng/issues/24) | Sicherheits- und API-Konzept erforderlich | Hardwareabschluss #10 |
| 4 | [#25 CLI für SDS-Versand](https://github.com/Nikoshi/DMRoute-ng/issues/25) | Planung nach Festlegung der Schnittstelle | #10 und #24 |
| 5 | [#15 Semantische DMR-Szenarien](https://github.com/Nikoshi/DMRoute-ng/issues/15) | Nach den Protokollarbeiten | #9 und #10 |
| 6 | [#21 Masterinitiierter Radio Check](https://github.com/Nikoshi/DMRoute-ng/issues/21) | Folgeticket | #9 und #15 |
| 7 | [#16 Testorchestrator](https://github.com/Nikoshi/DMRoute-ng/issues/16) | Planung erforderlich | #15 |
| 8 | [#14 Emulator-CLI/TUI](https://github.com/Nikoshi/DMRoute-ng/issues/14) | Planung erforderlich | stabile Szenario-API aus #15 |
| 9 | [#23 Nix Flake](https://github.com/Nikoshi/DMRoute-ng/issues/23) | WIP-Issue ohne festgelegte Anforderungen | Konzept offen |
| 10 | [#17 Virtuelles und echtes Funkgerät](https://github.com/Nikoshi/DMRoute-ng/issues/17) | Hardware-/RF-Konzept offen | #15 |

## 1. SDS-Echo-Dienst (#10)

### Aktueller Stand

- Der Checkpoint `d055e72` enthält TMS-, IPv4/UDP-, CRC-, BPTC-, Trellis- und
  DMRD-Writer sowie den bestätigten privaten Versand.
- TG `990099` beantwortet Gruppen-SDS `ECHO <Text>` lokal; reservierte
  Dienstframes werden nicht geroutet oder per MQTT als SMS veröffentlicht.
- Queue, Duplikatfenster und Transportzustand sind begrenzt und vorab reserviert.
- Capture-basierte Codec-, Router-, ACK- und Allocation-Tests bestehen.

### Nächster Schritt

- H10 aus dem Hardwaretestplan zuerst mit dem funktionierenden Hotspot für
  AnyTone und Retevis ausführen.
- Antwort, ACK beziehungsweise Timeout, höchstens drei Versuche, Duplikatschutz
  und Überlänge im PCAP bestätigen.
- Nach Reparatur des zweiten Hotspots nachweisen, dass Dienstframes lokal bleiben.
- Issue #10 erst nach der Hardwareabnahme schließen.

## 2. Radio Check (#9)

### Aktueller Stand

- Der Ein-Hotspot-Lauf vom 13. September bestätigt die Gerätefunktion, enthält
  aber keine UDP-seitig erfasste Antwort des Zielgeräts.
- Aus den zwei erfassten Preambles wird ausdrücklich kein Parser abgeleitet.
- Zwei Hotspots und vier Funkgeräte sind verfügbar; am zweiten Hotspot muss der
  gelöste Antennenstecker repariert werden.

### Nächster Schritt

- Nach der Reparatur den Zwei-Hotspot-Aufbau des Capture-Plans herstellen.
- Je drei erfolgreiche und unbeantwortete Prüfungen pro Geräterichtung isoliert
  mitschneiden.
- Anfrage, Antwort, Master-Rückleitung, Funkwiederholung, CRC und Timeout
  bestimmen und erst danach den Parser implementieren.

## 3. Authentifizierte SDS-Sendeschnittstelle (#24)

Auf Basis des hardwarebestätigten Senders aus #10 werden Authentifizierung,
Autorisierung, Transportprotokoll, Replay-Schutz, Rate-Limit, Request-ID,
Statusmodell und Fehlerantworten spezifiziert. Vor diesem Sicherheitskonzept wird
keine öffentliche HTTP- oder MQTT-Sendeschnittstelle implementiert.

## 4. CLI für SDS-Versand (#25)

Die spätere CLI verwendet ausschließlich die Schnittstelle aus #24. Sie erhält
nichtinteraktive Argumente, maschinenlesbare Ausgabe, sichere Zugangsdaten und
eindeutige Exit-Codes für Annahme, Zustellung, Timeout und Ablehnung.

## 5. Semantische DMR-Szenarien (#15)

Nach den bestätigten Paketformaten aus #9 und #10 soll der Emulator Radio Check,
Sprache und SDS aus semantischen Eingaben erzeugen, statt ausschließlich
aufgezeichnete DMRD-Frames abzuspielen. Gemeinsam nutzbare Writer bleiben vom
Serverprojekt entkoppelt.

## 6. Masterinitiierter Radio Check (#21)

Nach #9 und #15 soll der Master selbst einen Radio Check auslösen und Antwort,
Timeout, Wiederholungszahl und Dauer für interne Abläufe bereitstellen. Vorher
sind Masteridentität, Auslöser, Zielarten, begrenzter Zustand und Rate-Limit zu
entscheiden.

## 7. Testorchestrator (#16)

Ein unabhängiges Werkzeug soll mehrere Master mit dynamischen Ports und
definierter Topologie starten, beobachten und beenden. Die virtuellen Hotspots
und Funkgeräte aus #11 bilden die Teilnehmerseite.

## 8. Emulator-CLI/TUI (#14)

Nach Stabilisierung der Szenario-API werden Bedienmodell, sichere PSK-Behandlung,
Ausgabeformat und Abnahmekriterien festgelegt. Zuerst ist zwischen skriptbarer
CLI und direkter TUI zu entscheiden.

## 9. Nix Flake (#23)

Das Issue ist derzeit leer und als WIP markiert. Umfang, unterstützte Systeme,
Entwicklungsumgebung und Release-Bezug müssen vor einer Implementierung
festgelegt werden.

## 10. Virtuelles und echtes Funkgerät (#17)

Für die Kopplung eines virtuellen Teilnehmers mit echter RF-Hardware müssen
Nachrichtenrichtung, unterstützte Geräte, RF-Grenze und Sicherheitsanforderungen
geklärt werden. Die semantische Szenario-API aus #15 ist Voraussetzung.

## Weitere Hardwarevalidierung

- Gruppen-SDS mit zwei angemeldeten Hotspots verteilen und fehlendes Echo zum
  Ursprung nachweisen.
- Confirmed SDS vom Retevis erneut untersuchen; im bisherigen Versuch folgten
  auf den Datenheader keine Rate-3/4-Blöcke.
- Den Echo-Dienst mit AnyTone und Retevis sowie anschließend mit zwei Hotspots
  vollständig abnehmen.

## Abgeschlossen

- [#11 Virtueller Homebrew-Hotspot und Funkgerät für Tests](https://github.com/Nikoshi/DMRoute-ng/issues/11),
  umgesetzt mit [PR #18](https://github.com/Nikoshi/DMRoute-ng/pull/18).
- [#8 Config-Wizard](https://github.com/Nikoshi/DMRoute-ng/issues/8), umgesetzt
  mit [PR #13](https://github.com/Nikoshi/DMRoute-ng/pull/13).
- MQTT-Basis und Reconnect-Arbeiten aus #3 und #6 sind abgeschlossen.

## Pflege

- Bei Beginn eines Arbeitspakets Status, Branch und nächsten konkreten Schritt
  aktualisieren.
- Nach Protokoll- oder Hardwareerkenntnissen Abhängigkeiten und offene
  Entscheidungen nachführen.
- Nach einem PR-Merge den Eintrag nach „Abgeschlossen“ verschieben.
- GitHub-Issue, `TODO.md` und `HANDOFF.md` dürfen sich beim Status eines
  Arbeitspakets nicht widersprechen.
