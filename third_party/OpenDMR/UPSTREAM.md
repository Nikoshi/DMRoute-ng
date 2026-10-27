# Herkunft

Die Quellen stammen aus <https://github.com/MW0MWZ/OpenDMR> und sind auf den
Commit `d28164b39ba4d91ad5948ff22707937f8944f70f` (OpenDMR 1.0.0) festgelegt.

DMRoute-ng führt darauf einen lokalen Patch und kennzeichnet den Codec als
`1.0.0+dmroute.1`. Der Upstream-Encoder ordnet die neun quantisierten
Sprachparameter vor der Fehlerkorrektur linear an. AMBE verteilt mehrere
niederwertige Parameterbits dagegen auf den dritten und vierten Kanalblock.
Der Patch verwendet dafür die bereits im enthaltenen OP25-Encoder vorhandene
49-Bit-Zuordnung. Ein Regressionstest sichert den kanonischen AMBE-Frame für
einen definierten PCM-Stilleblock ab.
