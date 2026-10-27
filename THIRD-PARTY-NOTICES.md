# Hinweise zu Drittsoftware

## OpenDMR 1.0.0+dmroute.1

DMRoute-ng liefert für den Linux-x64-Playground optional den eigenständigen
OpenDMR-Codec aus. OpenDMR steht unter der GNU General Public License Version 2.
Der Codec läuft als separater Prozess; die DMRoute-ng-Programme werden nicht mit
seiner Bibliothek gelinkt.

- Projekt: <https://github.com/MW0MWZ/OpenDMR>
- Commit: `d28164b39ba4d91ad5948ff22707937f8944f70f`
- Lokaler Patch: korrigierte 49-Bit-Zuordnung der AMBE-Encoderparameter
- Lizenz: `third_party/OpenDMR/LICENSE`
- Decoder-Hinweise: `third_party/OpenDMR/decoder/CREDITS`
- Encoder-Hinweise: `third_party/OpenDMR/encoder/CREDITS`

Release-Pakete, die `dmr_codec` enthalten, enthalten außerdem die vollständigen
zugehörigen Quellen und diese Lizenz- und Urheberrechtshinweise.
