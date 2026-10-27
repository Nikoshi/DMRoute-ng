# DMRoute-ng

[![.NET Core Test & Coverage](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/test.yml/badge.svg?branch=main)](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/test.yml)
[![.NET Core Native Release](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/release.yml)

DMRoute-ng ist ein dezentraler IPv4-Master für Homebrew/MMDVM-Hotspots. Er
authentifiziert Hotspots, routet Gruppen- und Privatrufe zwischen Haushalten und
Zonen, verfolgt Roaming-Teilnehmer und veröffentlicht Betriebsdaten über MQTT.
SDS/SMS sowie AnyTone-Location-Telemetrie werden reassembliert; der lokale
SDS-Echo-Dienst stellt erstmals auch einen bestätigten Rückweg zum Funkgerät
bereit. Nicht direkt routbare Radio Checks suchen ihr Ziel zusätzlich über den
Heimat-Master und einen authentifizierten Mesh-WHOIS; bleibt das Ziel offline,
wird eine begrenzte Fallback-SDS dauerhaft vorgemerkt.

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
- [Radio-Check-Routing und Fallback-SDS](docs/components/radio-check-routing.adoc)
- [MQTT-Integration](docs/components/mqtt.adoc)
- [Location-Telemetrie und Privacy](docs/components/location-telemetry.adoc)
- [Hardwaretestplan](docs/hardware-test-plan.adoc)
- [Radio-Check-Resolver-Hardwaretest](docs/radio-check-resolver-hardware-plan.adoc)
- [Aufgabenplanung](TODO.md)

Die jeweils aktuelle Release-Version ist zusätzlich als PDF verfügbar:

- [DMRoute-ng-Handbuch.pdf](https://github.com/Nikoshi/DMRoute-ng/releases/latest/download/DMRoute-ng-Handbuch.pdf)
- [DMRoute-ng-Hardwaretests.pdf](https://github.com/Nikoshi/DMRoute-ng/releases/latest/download/DMRoute-ng-Hardwaretests.pdf)

## Entwicklung

Die reproduzierbare Entwicklungsumgebung wird mit Nix geladen. Sie enthält
.NET, C/C++, Rust und die Dokumentationswerkzeuge:

```console
nix develop
```

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

Der Linux-x64-Release wird vollständig aus der gesperrten Flake erzeugt:

```console
nix build .#releaseBundle
```

Auf Apple Silicon stehen Entwicklungs-Shell, OpenDMR, Dokumentation und der
optionale Rust-Encoder bereit. Server, Playground und Releasebundle sind auf
Linux x64 begrenzt. Aufbau und Pflege beschreibt die
[Build- und Release-Dokumentation](docs/components/build-release.adoc).

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
empfangene Nachrichten in einer Inbox. Call Alert und Radio Check funktionieren
in beide Richtungen einschließlich automatischer Bestätigung und eigener
Ereignis-Inbox. Ein typischer Lauf beginnt mit
`login all`, `radio announce all` und beispielsweise
`sds send radio1 tg 9 "Hallo"` oder
`radio check radio1 10001`. Tab-Completion,
Sitzungsverlauf und Ctrl+R erleichtern interaktive Tests. Die vollständige Bedienung beschreibt die
[Emulator-Dokumentation](docs/components/emulator.adoc). Der Test-Workflow stellt
zusätzlich self-contained Single-File-Artefakte für Linux x64 und Windows x64
bereit. Das Linux-x64-Release enthält zusätzlich den getrennten OpenDMR-Codec
und dessen vollständige GPL-Quellen. Damit wandelt der Playground WAV-Dateien
in reproduzierbare DMRD-Sprachszenarien um und dekodiert unverschlüsselte
Sprachszenarien wieder nach WAV. Build, Bedienung und Grenzen stehen ebenfalls
in der Emulator-Dokumentation. Für Encodervergleiche kann im Quellbaum außerdem
ein experimentelles, getrennt konfiguriertes `blip25-vocoder`-Backend gebaut
werden. Es gehört derzeit nicht zum Releasepaket.
