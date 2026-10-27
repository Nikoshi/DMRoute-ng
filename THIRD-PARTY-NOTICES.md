# Hinweise zu Drittsoftware

## OpenDMR 1.0.0+dmroute.2

DMRoute-ng liefert für den Linux-x64-Playground optional den eigenständigen
OpenDMR-Codec aus. OpenDMR steht unter der GNU General Public License Version 2.
Der Codec läuft als separater Prozess; die DMRoute-ng-Programme werden nicht mit
seiner Bibliothek gelinkt.

- Projekt: <https://github.com/MW0MWZ/OpenDMR>
- Commit: `d28164b39ba4d91ad5948ff22707937f8944f70f`
- Lokale Patches: korrigierte 49-Bit-Zuordnung, OP25-V/UV-Bandabbildung und
  DMR-Gain-Kalibrierung der AMBE-Encoderparameter
- Lizenz: `third_party/OpenDMR/LICENSE`
- Decoder-Hinweise: `third_party/OpenDMR/decoder/CREDITS`
- Encoder-Hinweise: `third_party/OpenDMR/encoder/CREDITS`

Release-Pakete, die `dmr_codec` enthalten, enthalten außerdem die vollständigen
zugehörigen Quellen und diese Lizenz- und Urheberrechtshinweise.

## blip25-vocoder 1.0.0

Das optionale Entwicklungswerkzeug `dmroute-blip25-encoder` verwendet
`blip25-vocoder` als eigenständige Rust-Abhängigkeit. Das Werkzeug wird derzeit
nicht mit regulären DMRoute-ng-Releases ausgeliefert.

- Projekt: <https://github.com/openBLIP25/blip25-vocoder>
- geprüfter Commit zur Version 1.0.0:
  `d1762813b7142c021f4105c832bf7f060d1249fa`
- Crate: `blip25-vocoder = 1.0.0`, Prüfsumme in `Cargo.lock`
- Lizenz: MIT

Das Upstream-Projekt beschreibt seinen Analyse-/Synthese-Kern ausdrücklich als
Reverse Engineering eines Referenz-Software-Vocoders. Es handelt sich daher
nicht um eine von DMRoute-ng behauptete Clean-Room-Implementierung. Der
Upstream-Patenthinsweis nennt für AMBE+2 das US-Patent 8359197 und ein erwartetes
Laufzeitende am 20. Mai 2028. Vor einer Verteilung oder produktiven Nutzung sind
Lizenz- und Patentlage für den jeweiligen Rechtsraum und Einsatzzweck zu prüfen.
