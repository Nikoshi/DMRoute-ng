# DMRoute-ng-Emulatorkern

Die zentrale und als PDF veröffentlichte Beschreibung des Emulatorkerns befindet
sich in der [Emulator-Dokumentation](../../docs/components/emulator.adoc).

Der interaktive Playground verbindet mehrere virtuelle Hotspots mit einem
laufenden Master. Virtuelle Funkgeräte senden und empfangen UTF-16LE-SDS über
dieselben gemeinsam mit dem Server verwendeten Protokollcodecs. Bestätigte
Nachrichten verwenden die auf realer Hardware beobachteten vollständigen
TMS-Zustellberichte; der einzelne DMR-Response-Header bleibt als Fallback:

```console
make playground PLAYGROUND_ARGS='--setup --config dmroute-playground.json'
make playground PLAYGROUND_ARGS='--config dmroute-playground.json'
```
