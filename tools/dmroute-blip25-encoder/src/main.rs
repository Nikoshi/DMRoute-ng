use std::{env, fs, io, process};

use blip25_vocoder::halfrate::frame::{encode_code_vectors, CODE_WIDTHS};
use blip25_vocoder::vocoder::{Rate, Vocoder, FRAME_SAMPLES};

const AMBE_BYTES_PER_FRAME: usize = 9;
const PCM_BYTES_PER_FRAME: usize = FRAME_SAMPLES * size_of::<i16>();

fn main() {
    if let Err(error) = run() {
        eprintln!("Fehler: {error}");
        process::exit(2);
    }
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let arguments: Vec<String> = env::args().collect();
    match arguments.as_slice() {
        [_, command] if command == "info" => {
            println!("dmroute-blip25-encoder 0.1.0 (blip25-vocoder 1.0.0, AMBE+2 3600x2450)");
            Ok(())
        }
        [_, command, input, output] if command == "encode" => encode_file(input, output),
        _ => Err(
            "Verwendung: dmroute-blip25-encoder info | encode <eingabe.raw> <ausgabe.ambe>".into(),
        ),
    }
}

fn encode_file(input_path: &str, output_path: &str) -> Result<(), Box<dyn std::error::Error>> {
    let input = fs::read(input_path)?;
    if input.is_empty() || input.len() % PCM_BYTES_PER_FRAME != 0 {
        return Err(format!(
            "PCM muss aus vollständigen 20-ms-Frames mit je {FRAME_SAMPLES} Little-Endian-Samples bestehen."
        )
        .into());
    }

    let encoded = encode_pcm(&input)?;
    fs::write(output_path, encoded)?;
    Ok(())
}

fn encode_pcm(input: &[u8]) -> Result<Vec<u8>, io::Error> {
    let frame_count = input.len() / PCM_BYTES_PER_FRAME;
    let mut output = Vec::with_capacity(frame_count * AMBE_BYTES_PER_FRAME);
    let mut vocoder = Vocoder::new(Rate::HalfRate3600x2450);
    let mut samples = [0_i16; FRAME_SAMPLES];

    for (frame_index, raw_frame) in input.chunks_exact(PCM_BYTES_PER_FRAME).enumerate() {
        read_pcm_frame(raw_frame, &mut samples);
        let code = encode_frame(&mut vocoder, &samples)?;
        // Der Streaming-Encoder liefert zunächst seinen internen Stille-Preroll.
        // Ab dem zweiten Aufruf gehört die Ausgabe zum jeweils vorigen PCM-Frame.
        if frame_index != 0 {
            output.extend_from_slice(&code);
        }
    }

    // Ein abschließender Stille-Frame leert genau den letzten gehaltenen Sprachframe.
    output.extend_from_slice(&encode_frame(&mut vocoder, &[0_i16; FRAME_SAMPLES])?);
    debug_assert_eq!(output.len(), frame_count * AMBE_BYTES_PER_FRAME);
    Ok(output)
}

fn read_pcm_frame(raw: &[u8], samples: &mut [i16; FRAME_SAMPLES]) {
    for (sample, bytes) in samples.iter_mut().zip(raw.chunks_exact(2)) {
        *sample = i16::from_le_bytes([bytes[0], bytes[1]]);
    }
}

fn encode_frame(vocoder: &mut Vocoder, samples: &[i16]) -> Result<[u8; 9], io::Error> {
    let information: [u16; 4] = vocoder
        .encode_info(samples)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error.to_string()))?
        .try_into()
        .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "unerwartete Parameterzahl"))?;
    Ok(pack_code_vectors(&encode_code_vectors(&information)))
}

/// Packt C0(24), C1(23), C2(11), C3(14) MSB-first in das kanonische
/// 72-Bit-Codewort, das auch der Playground und OpenDMR verwenden.
fn pack_code_vectors(code: &[u32; 4]) -> [u8; AMBE_BYTES_PER_FRAME] {
    let mut output = [0_u8; AMBE_BYTES_PER_FRAME];
    let mut output_bit = 0_usize;
    for (word_index, word) in code.iter().enumerate() {
        for word_bit in (0..usize::from(CODE_WIDTHS[word_index])).rev() {
            output[output_bit / 8] |= (((word >> word_bit) & 1) as u8) << (7 - output_bit % 8);
            output_bit += 1;
        }
    }
    output
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn packs_canonical_code_vector_widths_msb_first() {
        let packed = pack_code_vectors(&[0x00ab_cdef, 0x0055_5555, 0x0555, 0x2aaa]);
        assert_eq!(
            packed,
            [0xab, 0xcd, 0xef, 0xaa, 0xaa, 0xab, 0x55, 0x6a, 0xaa]
        );
    }

    #[test]
    fn compensates_stream_delay_and_emits_one_codeword_per_pcm_frame() {
        for frame_count in [1, 2, 3, 9] {
            let input = vec![0_u8; frame_count * PCM_BYTES_PER_FRAME];
            let output = encode_pcm(&input).expect("Stille muss kodierbar sein");
            assert_eq!(output.len(), frame_count * AMBE_BYTES_PER_FRAME);
        }
    }
}
