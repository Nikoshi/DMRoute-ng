/*
 * OpenDMR encoder regression test
 *
 * Verifies the canonical AMBE frame produced for the first 20 ms of silence.
 * The vector detects accidental linear packing of the nine quantizer values;
 * AMBE channel order distributes their low bits across C2 and C3.
 */

#include "opendmr.h"

#include <cstdio>
#include <cstring>

int main()
{
    const uint8_t expected[OPENDMR_AMBE_FRAME_BYTES] = {
        0xB0U, 0x35U, 0x7CU, 0x1EU, 0xB7U, 0x8EU, 0x9FU, 0x05U, 0xC5U
    };
    int16_t pcm[OPENDMR_PCM_SAMPLES] = {0};
    uint8_t actual[OPENDMR_AMBE_FRAME_BYTES] = {0};

    opendmr_encoder_t *encoder = opendmr_encoder_create();
    if (encoder == nullptr) {
        std::fputs("encoder creation failed\n", stderr);
        return 1;
    }

    const bool encoded = opendmr_encode(encoder, pcm, actual);
    opendmr_encoder_destroy(encoder);
    if (!encoded) {
        std::fputs("silence encoding failed\n", stderr);
        return 1;
    }

    if (std::memcmp(actual, expected, sizeof(expected)) != 0) {
        std::fputs("unexpected canonical AMBE silence frame:", stderr);
        for (const uint8_t value : actual)
            std::fprintf(stderr, " %02X", value);
        std::fputc('\n', stderr);
        return 1;
    }

    std::puts("OpenDMR encoder regression test passed");
    return 0;
}
