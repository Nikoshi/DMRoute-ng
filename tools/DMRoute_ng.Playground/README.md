# DMRoute-ng Emulator-Playground

Der Playground verbindet mehrere virtuelle Homebrew-Hotspots mit einem bereits
laufenden DMRoute-ng-Master. Das Profil wird interaktiv erstellt:

```console
DMRoute-ng-playground --setup --config dmroute-playground.json
DMRoute-ng-playground --config dmroute-playground.json
```

In der REPL zeigt `help` alle Befehle. Das Profil enthält die Homebrew-PSKs im
Klartext und darf nicht veröffentlicht werden. Capture-Beispiele befinden sich
im Verzeichnis `scenarios/`.

Nach `login all` können virtuelle Funkgeräte mit `radio announce all` für
private Zustellung bekannt gemacht werden. Beispiele:

```text
sds send radio1 tg 9 "Hallo Gruppe"
sds send radio1 fug 10002 "Hallo Funkgerät"
sds send radio1 service 990099 "ECHO Playground"
sds inbox radio1
sds watch radio1
radio alert radio1 10002
radio check radio1 10002
radio events radio1
radio watch radio1
```

Im interaktiven Terminal stehen `Tab`-Completion, Verlauf mit `Pfeil hoch` und
`Pfeil runter` sowie Rückwärtssuche mit `Ctrl+R` bereit. Bestätigte private SDS
warten 2,5 Sekunden auf einen TMS-Zustellbericht, bevor der nächste Versuch
beginnt. Call Alert und Radio Check verwenden dasselbe Antwortfenster und werden
von virtuellen Zielgeräten automatisch bestätigt.

Für Sprachszenarien wird OpenDMR als getrenntes Programm gebaut:

```console
make opendmr-codec
DMRoute-ng-playground --config dmroute-playground.json \
  --open-dmr artifacts/opendmr/dmr_codec \
  voice encode radio1 tg 9 ansage.wav ansage.dmrd
DMRoute-ng-playground --open-dmr artifacts/opendmr/dmr_codec \
  voice decode ansage.dmrd kontrolle.wav
```

Ein optionaler Encoder kann unabhängig vom OpenDMR-Decoder gewählt werden:

```console
make blip25-encoder
DMRoute-ng-playground --open-dmr artifacts/opendmr/dmr_codec \
  --voice-encoder tools/dmroute-blip25-encoder/target/release/dmroute-blip25-encoder \
  voice encode radio1 tg 9 ansage.wav ansage.dmrd
```

`--voice-encoder` betrifft nur `voice encode`. Für `voice decode` bleibt
OpenDMR zuständig.

Die Eingabe ist einkanaliges PCM-WAV mit 8000 Hz und 16 Bit. Das erzeugte
Szenario wird anschließend in der REPL mit `run <hotspot> ansage.dmrd`
abgespielt. Nur vollständige unverschlüsselte Sprachrufe werden dekodiert.

Die vollständige Beschreibung steht in der
[Emulator-Dokumentation](https://github.com/Nikoshi/DMRoute-ng/blob/main/docs/components/emulator.adoc).
