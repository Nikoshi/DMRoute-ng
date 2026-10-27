# Experimenteller AMBE+2-Encoder

Dieses eigenständige Werkzeug kodiert rohe PCM-Daten mit 8000 Hz, Mono und
16 Bit Little Endian in kanonische AMBE+2-Codewörter mit jeweils 9 Byte. Es
dient ausschließlich als optionales Encoder-Backend des Playgrounds. Der
OpenDMR-Decoder bleibt davon unberührt.

```console
cargo build --manifest-path tools/dmroute-blip25-encoder/Cargo.toml --release
target/release/dmroute-blip25-encoder info
target/release/dmroute-blip25-encoder encode eingabe.raw ausgabe.ambe
```

Die Abhängigkeit `blip25-vocoder` ist auf Version 1.0.0 festgelegt. Das Projekt
beschreibt den Codec-Kern ausdrücklich als Reverse Engineering eines
Referenz-Vocoders und weist auf das AMBE+2-Patent US8359197 hin. Lizenz,
Herkunft und Nutzungsgrenzen sind in `THIRD-PARTY-NOTICES.md` dokumentiert.
