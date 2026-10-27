# Herkunft

Die Quellen stammen aus <https://github.com/MW0MWZ/OpenDMR> und sind auf den
Commit `d28164b39ba4d91ad5948ff22707937f8944f70f` (OpenDMR 1.0.0) festgelegt.

DMRoute-ng führt darauf lokale Patches und kennzeichnet den Codec als
`1.0.0+dmroute.2`. Der Upstream-Encoder ordnet die neun quantisierten
Sprachparameter vor der Fehlerkorrektur linear an. AMBE verteilt mehrere
niederwertige Parameterbits dagegen auf den dritten und vierten Kanalblock.
Die lokale Fassung verwendet dafür die bereits im enthaltenen OP25-Encoder
vorhandene 49-Bit-Zuordnung. Zusätzlich übernimmt sie die DMR-Kalibrierung
`gain_adjust = 3.0` und die V/UV-Bandzuordnung aus OP25. Der öffentliche
dB-Wert bleibt relativ zu dieser Kalibrierung und wird in den logarithmischen
Offset des Quantisierers umgerechnet. Regressionstests sichern feste AMBE-
Vektoren für Stille und ein warmgelaufenes synthetisches Sprachsignal sowie
das Reset- und Gainverhalten ab.
