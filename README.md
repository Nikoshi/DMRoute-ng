# DMRoute-ng

[![.NET Core Test & Coverage](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/test.yml/badge.svg?branch=main)](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/test.yml)
[![.NET Core Native Release](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/release.yml/badge.svg?branch=main)](https://github.com/Nikoshi/DMRoute-ng/actions/workflows/release.yml)

## Konfiguration

Der interaktive Setup-Wizard erstellt standardmäßig die Datei `dmroute.json`:

```console
DMRoute-ng --setup
```

Ein abweichender Pfad wird mit `--config` angegeben. Der Wizard speichert die
Konfiguration und beendet sich. Der Dienst wird anschließend mit derselben Datei
gestartet:

```console
DMRoute-ng --setup --config /etc/dmroute/dmroute.json
DMRoute-ng --config /etc/dmroute/dmroute.json
```

Die bisherigen Konfigurationsargumente der Kommandozeile bleiben gültig. Sie
werden zuletzt ausgewertet und überschreiben damit Werte aus JSON-Dateien und
Umgebungsvariablen:

```console
DMRoute-ng --config dmroute.json --Mqtt:Host mqtt.example.net --ZoneId=101
```

Die Priorität steigt in dieser Reihenfolge: eingebaute Standardwerte,
`appsettings.json`, die mit `--config` gewählte Datei, Umgebungsvariablen und
Kommandozeilenargumente. Alle Schlüssel und Bedienelemente beschreibt die
[Konfigurationsreferenz](docs/components/configuration.adoc).
