# DMRoute-ng

[![.NET Core Test & Coverage](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/test.yml/badge.svg?branch=main)](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/test.yml)
[![.NET Core Native Release](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/release.yml)

DMRoute-ng ist ein dezentraler IPv4-Master für Homebrew/MMDVM-Hotspots. Er
authentifiziert Hotspots, routet Gruppen- und Privatrufe zwischen Haushalten und
Zonen, verfolgt Roaming-Teilnehmer und veröffentlicht Betriebsdaten über MQTT.
SDS/SMS sowie AnyTone-Location-Telemetrie werden reassembliert; der lokale
SDS-Echo-Dienst stellt erstmals auch einen bestätigten Rückweg zum Funkgerät
bereit.

## Schnellstart

Fertige selbstenthaltende NativeAOT-Binaries für Linux x64 und Windows x64 stehen
unter [Releases](https://github.com/Nikoshi/DMRoute-ng/releases/latest) bereit.
Die vollständige Inbetriebnahme beschreibt der
[Quick Start](docs/quick-start.adoc).

```console
./DMRoute-ng --setup --config ./dmroute.json
./DMRoute-ng --config ./dmroute.json
```

Der Wizard benötigt ein interaktives Terminal. Pfeiltasten wählen ein Feld,
`Enter` bearbeitet es, `Leertaste` schaltet boolesche Werte um, `A` zeigt die
erweiterten Einstellungen und `F10` oder `S` speichert.

Ein abweichender Konfigurationspfad wird mit `--config` angegeben. Bestehende
.NET-Konfigurationsargumente bleiben gültig und überschreiben Werte aus Datei
und Umgebung:

```console
./DMRoute-ng --config dmroute.json --Mqtt:Host mqtt.example.net --ZoneId=101
```

Die Priorität steigt von eingebauten Standardwerten über `appsettings.json`, die
gewählte JSON-Datei und Umgebungsvariablen bis zu Kommandozeilenargumenten.

## Dokumentation

- [Quick Start](docs/quick-start.adoc)
- [Konfiguration und Setup-Wizard](docs/components/configuration.adoc)
- [SDS-Echo-Dienst](docs/components/sds-echo.adoc)
- [MQTT-Integration](docs/components/mqtt.adoc)
- [Location-Telemetrie und Privacy](docs/components/location-telemetry.adoc)
- [Hardwaretestplan](docs/hardware-test-plan.adoc)
- [Aufgabenplanung](TODO.md)

Die jeweils aktuelle Release-Version ist zusätzlich als PDF verfügbar:

- [DMRoute-ng-Handbuch.pdf](https://github.com/Nikoshi/DMRoute-ng/releases/latest/download/DMRoute-ng-Handbuch.pdf)
- [DMRoute-ng-Hardwaretests.pdf](https://github.com/Nikoshi/DMRoute-ng/releases/latest/download/DMRoute-ng-Hardwaretests.pdf)

## Entwicklung

Build und Tests laufen standardmäßig in Release-Konfiguration:

```console
make test
```

Für die Dokumentation werden Ruby, Bundler und die im `Gemfile.lock` fixierten
Abhängigkeiten benötigt:

```console
bundle install
make docs
```

Die erzeugten HTML- und PDF-Dateien liegen anschließend unter
`artifacts/docs/` und werden nicht eingecheckt.

## Emulator-Playground

Der experimentelle Playground verbindet mehrere virtuelle Homebrew-Hotspots mit
einem bereits laufenden Master. Setup und Start erfolgen aus dem Quellbaum mit:

```console
make playground PLAYGROUND_ARGS='--setup --config dmroute-playground.json'
make playground PLAYGROUND_ARGS='--config dmroute-playground.json'
```

Login, Ping, Disconnect, Raw-DMRD und Capture-Szenarien lassen sich anschließend
interaktiv testen. Profilbasierte virtuelle Funkgeräte senden unbestätigte SDS
an Talkgroups, bestätigte SDS an reale oder virtuelle Teilnehmer und halten
empfangene Nachrichten in einer Inbox. Ein typischer Lauf beginnt mit
`login all`, `radio announce all` und beispielsweise
`sds send radio1 tg 9 "Hallo"` oder
`sds send radio1 service 990099 "ECHO Hallo"`. Tab-Completion,
Sitzungsverlauf und Ctrl+R erleichtern interaktive Tests. Die vollständige Bedienung beschreibt die
[Emulator-Dokumentation](docs/components/emulator.adoc). Der Test-Workflow stellt
zusätzlich self-contained Single-File-Artefakte für Linux x64 und Windows x64
bereit; sie sind bewusst noch keine regulären Release-Dateien.
