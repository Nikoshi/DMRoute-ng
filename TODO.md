# Aufgabenplanung

Stand: 24. Oktober 2026

Diese Datei fasst die geplanten Arbeiten und ihre Abhängigkeiten zusammen. Die
GitHub-Issues bleiben die verbindliche Quelle für Anforderungen und Diskussionen.
Technische Zwischenstände und Übergaben werden in `HANDOFF.md` dokumentiert.

## Übersicht

| Priorität | Thema | Status | Abhängigkeiten |
|---:|---|---|---|
| 1 | [#10 SDS-Echo-Dienst](https://github.com/Nikoshi/DMRoute-ng/issues/10) | Encoder implementiert; Geräteabnahme `DC05` bis `DC09` offen | funktionierender Hotspot; AnyTone und RT3S |
| 2 | [#9 Radio Check](https://github.com/Nikoshi/DMRoute-ng/issues/9) | Golden Vector vorhanden; `DC01` bis `DC04` und `DC10` offen | für `DC10` zwei getrennte RF-Endpunkte |
| 3 | [#24 Authentifizierte SDS-Sendeschnittstelle](https://github.com/Nikoshi/DMRoute-ng/issues/24) | Sicherheits- und API-Konzept erforderlich | Hardwareabschluss #10 |
| 4 | [#25 CLI für SDS-Versand](https://github.com/Nikoshi/DMRoute-ng/issues/25) | Planung nach Festlegung der Schnittstelle | #10 und #24 |
| 5 | [#15 Semantische DMR-Szenarien](https://github.com/Nikoshi/DMRoute-ng/issues/15) | SDS, Call Alert und Radio Check umgesetzt; Sprache offen | #9 und #10 |
| 6 | [#21 Masterinitiierter Radio Check](https://github.com/Nikoshi/DMRoute-ng/issues/21) | Resolver, Blind-Probe, WHOIS und Offline-Fallback umgesetzt; Ein-Master-Hardwarelauf teilweise bestanden | #9 und #15 |
| 7 | [#16 Testorchestrator](https://github.com/Nikoshi/DMRoute-ng/issues/16) | Planung erforderlich | #15 |
| 8 | [#14 Emulator-CLI/TUI](https://github.com/Nikoshi/DMRoute-ng/issues/14) | REPL mit SDS und Control-Transaktionen umgesetzt; TUI optional | weitere Szenario-APIs aus #15 |
| 9 | [#23 Nix Flake](https://github.com/Nikoshi/DMRoute-ng/issues/23) | WIP-Issue ohne festgelegte Anforderungen | Konzept offen |
| 10 | [#17 Virtuelles und echtes Funkgerät](https://github.com/Nikoshi/DMRoute-ng/issues/17) | Hardware-/RF-Konzept offen | #15 |

## Gerätekompatibilität AnyTone und RT3S

DMRoute-ng wählt kein Herstellerprofil anhand einer DMR-ID. Der gemeinsame
Sendepfad akzeptiert automatisch die capture-verifizierten Antwortvarianten.
Das AnyTone verwendet für positive SDS-Zustellung einen vollständigen
TMS-Delivery-Report; beim RT3S wurde ein positiver bestätigter
DMR-Response-Header beobachtet. Beide Varianten müssen eine Transaktion genau
einmal abschließen. Bei einem unbeantworteten Radio Check greifen Timeout,
Negativ-Cache und Fallback-SDS.

Der vollständige Ablauf steht im
[Gerätekompatibilitäts-Testplan](docs/device-compatibility-hardware-plan.adoc)
(`DC01` bis `DC10`). Etappe A verwendet den funktionierenden physischen Hotspot
und einen getrennten Playground-Hotspot. Etappe B folgt nach Reparatur des
zweiten Hotspots mit zwei isolierten RF-Endpunkten.

## Codepflege

- Der Branch `refactor/application-structure` gliedert Radio-Check-Resolver,
  Router, MQTT-Integration und SDS-Echo nach Verantwortung, ohne das
  Laufzeitverhalten oder öffentliche Signaturen zu ändern.
- Fachliche Codekommentare beschreiben Protokollformate, geliehene Spans,
  Pufferbesitz, begrenzte Zustände und Replay-Schutz.
- Der Aufräum-PR wird auf Draft-PR #26 gestapelt und nach dessen Merge auf
  `main` umgestellt.

## 1. SDS-Echo-Dienst (#10)

### Aktueller Stand

- Der Checkpoint `d055e72` enthält TMS-, IPv4/UDP-, CRC-, BPTC-, Trellis- und
  DMRD-Writer sowie den bestätigten privaten Versand.
- TG `990099` beantwortet Gruppen-SDS `ECHO <Text>` lokal; reservierte
  Dienstframes werden nicht geroutet oder per MQTT als SMS veröffentlicht.
- Queue, Duplikatfenster und Transportzustand sind begrenzt und vorab reserviert.
- Capture-basierte Codec-, Router-, ACK- und Allocation-Tests bestehen.

### Nächster Schritt

- `DC05` bis `DC09` aus dem Gerätekompatibilitäts-Testplan zuerst mit dem
  funktionierenden Hotspot für AnyTone und RT3S ausführen.
- Antwort, ACK beziehungsweise Timeout, höchstens drei Versuche, Duplikatschutz
  und Überlänge im PCAP bestätigen.
- Nach Reparatur des zweiten Hotspots nachweisen, dass Dienstframes lokal bleiben.
- Issue #10 erst nach der Hardwareabnahme schließen.

## 2. Radio Check (#9)

### Aktueller Stand

- Ein antwortorientierter Ein-Hotspot-Lauf vom 21. September erfasst Request und
  positive Antwort für Call Alert und Radio Check vollständig.
- Capture-basierte Writer und Parser werden im Emulator verwendet; die passive
  Produktionsauswertung aus #9 bleibt von diesem Branch getrennt.
- Zwei Hotspots und vier Funkgeräte sind verfügbar; am zweiten Hotspot muss der
  gelöste Antennenstecker repariert werden.

### Nächster Schritt

- `DC01` bis `DC04` zunächst mit physischem Ziel und virtuellem Quell-Hotspot
  ausführen.
- Nach der Reparatur `DC10` mit zwei getrennten RF-Endpunkten durchführen.
- Je drei erfolgreiche und unbeantwortete Prüfungen pro Geräterichtung isoliert
  mitschneiden.
- Anfrage, Antwort, Master-Rückleitung, Funkwiederholung und Timeout in beiden
  Geräterichtungen bestimmen und danach die Produktionsauswertung umsetzen.

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

Der Emulator erzeugt SDS bereits aus semantischen Eingaben: unbestätigt an
Talkgroups und bestätigt mit TMS-Zustellbericht sowie ACK/NACK-Fallback an
einzelne Teilnehmer. Empfang, TMS-Dekodierung, Inbox und automatische
Bestätigung sind ebenfalls umgesetzt. Der Playground bietet dafür zusätzlich
Service-Ziele, Completion, Verlauf und Ctrl+R-Suche.
Call Alert und Radio Check funktionieren capture-basiert in beide Richtungen,
einschließlich automatischer Antwort, Retry/Timeout und Ereignis-Inbox.
Semantische Sprache bleibt offen.
Gemeinsam genutzte Codecs liegen in `DMRoute_ng.Protocol` und bleiben von der
Serverassembly entkoppelt.

## 6. Masterinitiierter Radio Check (#21)

Nicht routbare Radio Checks verwenden nun lokal bekannte Gäste, den
Heimat-Master, eine aktive Blind-Probe über dessen lokale Hotspots, dessen
frischere lokale/Roaming-Beobachtung und anschließend einen authentifizierten
WHOIS-Fanout an bekannte direkte Peers. Der Ziel-Master rekonstruiert die
capture-verifizierte Control-Sequenz; nur das FuG kann positiv antworten. Ohne
Pfad wird eine bestätigte SDS von `990098` bis zu 24 Stunden in einer
dauerhaften, begrenzten Outbox gehalten. Positive TMS-Reports und bestätigte
DMR-Response-Header quittieren die Zustellung.

Der Ein-Master-Lauf vom 21. September 2026 bestätigte Blind-Probe, AnyTone-
Kontrollpfad und Zustellung der Fallback-SDS. Das RT3S beantwortete den Radio
Check nicht; sein positiver DMR-Response-Header führte zum ergänzten
Kompatibilitätspfad. Offen bleiben dessen Hardware-Wiederholung, eine positive
Blind-Probe mit geeignetem FuG, die Mehrzonenabnahme und ein eigenständiger
administrativer/API-Trigger.

Der ausführbare Ablauf für den heutigen Ein-Master-Test und die anschließende
Zwei-Master-Matrix steht in
`docs/radio-check-resolver-hardware-plan.adoc` (RCR01 bis RCR09).
Die bisherigen Ergebnisse stehen in
`docs/hardware-test-results-2026-09-21-radio-check-resolver.adoc`.

## 7. Testorchestrator (#16)

Ein unabhängiges Werkzeug soll mehrere Master mit dynamischen Ports und
definierter Topologie starten, beobachten und beenden. Die virtuellen Hotspots
und Funkgeräte aus #11 bilden die Teilnehmerseite.

## 8. Emulator-CLI/TUI (#14)

Der REPL-Playground verwaltet mehrere virtuelle Hotspots und Funkgeräte, führt
Login, Ping, Disconnect und explizite Radio-Ankündigungen aus, sendet SDS und
Raw-DMRD, spielt Capture-Szenarien ab und zeigt Frames sowie SDS-Inboxen an.
Profile werden lokal mit restriktiven Dateirechten gespeichert; Linux- und
Windows-Builds entstehen als CI-Artefakte.

Semantische Befehle für Sprache folgen nach den Writern aus #15. Die
Control-Befehle sind inzwischen umgesetzt; nach der semantischen
Sprachunterstützung wird bewertet, ob die REPL genügt oder eine Vollbild-TUI
echten Mehrwert bietet.

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
- Den DMR-ACK-Kompatibilitätspfad der Fallback-SDS erneut mit dem RT3S prüfen
  und eine positive Blind-Probe mit einem Radio-Check-fähigen FuG aus Zone 101
  aufzeichnen.

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
