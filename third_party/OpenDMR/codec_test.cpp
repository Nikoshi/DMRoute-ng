/*
 * OpenDMR encoder regression tests
 *
 * The vectors protect the canonical AMBE channel layout and the calibrated
 * DMR gain. The voiced vector is evaluated after encoder warm-up because the
 * speech analysis and AMBE predictor intentionally retain frame state.
 */

#include "opendmr.h"

#include <cstdio>
#include <cstring>

static bool expect_frame(const char *name,
                         const uint8_t actual[OPENDMR_AMBE_FRAME_BYTES],
                         const uint8_t expected[OPENDMR_AMBE_FRAME_BYTES])
{
    if (std::memcmp(actual, expected, OPENDMR_AMBE_FRAME_BYTES) == 0)
        return true;

    std::fprintf(stderr, "%s:", name);
    for (int index = 0; index < OPENDMR_AMBE_FRAME_BYTES; index++)
        std::fprintf(stderr, " %02X", actual[index]);
    std::fputc('\n', stderr);
    return false;
}

static bool encode_silence_vector()
{
    const uint8_t expected[OPENDMR_AMBE_FRAME_BYTES] = {
        0xB0U, 0x1CU, 0x42U, 0x6DU, 0xDCU, 0xF8U, 0x9FU, 0x05U, 0xC5U
    };
    int16_t pcm[OPENDMR_PCM_SAMPLES] = {0};
    uint8_t actual[OPENDMR_AMBE_FRAME_BYTES] = {0};

    opendmr_encoder_t *encoder = opendmr_encoder_create();
    if (encoder == nullptr)
        return false;

    const bool encoded = opendmr_encode(encoder, pcm, actual);
    opendmr_encoder_destroy(encoder);
    return encoded && expect_frame("unexpected canonical AMBE silence frame", actual, expected);
}

static void fill_voiced_frame(int16_t pcm[OPENDMR_PCM_SAMPLES])
{
    /* Integer samples for one 200-Hz fundamental period with two harmonics. */
    static const int16_t period[40] = {
        0, 4555, 8655, 11913, 14074, 15046, 14903, 13860, 12225, 10337,
        8500, 6937, 5759, 4960, 4441, 4046, 3612, 3014, 2189, 1156,
        0, -1156, -2189, -3014, -3612, -4046, -4441, -4960, -5759, -6937,
        -8500, -10337, -12225, -13860, -14903, -15046, -14074, -11913,
        -8655, -4555
    };

    for (int index = 0; index < OPENDMR_PCM_SAMPLES; index++)
        pcm[index] = period[index % 40];
}

static bool encode_warmed_voiced_frame(opendmr_encoder_t *encoder,
                                       uint8_t output[OPENDMR_AMBE_FRAME_BYTES])
{
    int16_t pcm[OPENDMR_PCM_SAMPLES];
    fill_voiced_frame(pcm);

    for (int frame = 0; frame < 12; frame++) {
        if (!opendmr_encode(encoder, pcm, output))
            return false;
    }
    return true;
}

static bool encode_voiced_and_gain_vectors()
{
    const uint8_t expected[OPENDMR_AMBE_FRAME_BYTES] = {
        0xE6U, 0x27U, 0x4CU, 0x3AU, 0x1EU, 0x67U, 0x5FU, 0x18U, 0xE5U
    };
    uint8_t initial[OPENDMR_AMBE_FRAME_BYTES] = {0};
    uint8_t after_reset[OPENDMR_AMBE_FRAME_BYTES] = {0};
    uint8_t explicit_zero[OPENDMR_AMBE_FRAME_BYTES] = {0};
    uint8_t boosted[OPENDMR_AMBE_FRAME_BYTES] = {0};
    uint8_t reduced[OPENDMR_AMBE_FRAME_BYTES] = {0};

    opendmr_encoder_t *encoder = opendmr_encoder_create();
    opendmr_encoder_t *zero_encoder = opendmr_encoder_create();
    opendmr_encoder_t *boost_encoder = opendmr_encoder_create();
    opendmr_encoder_t *reduce_encoder = opendmr_encoder_create();
    if (encoder == nullptr || zero_encoder == nullptr ||
        boost_encoder == nullptr || reduce_encoder == nullptr) {
        opendmr_encoder_destroy(encoder);
        opendmr_encoder_destroy(zero_encoder);
        opendmr_encoder_destroy(boost_encoder);
        opendmr_encoder_destroy(reduce_encoder);
        return false;
    }

    opendmr_encoder_set_gain(zero_encoder, 0);
    opendmr_encoder_set_gain(boost_encoder, 6);
    opendmr_encoder_set_gain(reduce_encoder, -6);

    bool valid = encode_warmed_voiced_frame(encoder, initial);
    opendmr_encoder_reset(encoder);
    valid = valid && encode_warmed_voiced_frame(encoder, after_reset);
    valid = valid && encode_warmed_voiced_frame(zero_encoder, explicit_zero);
    valid = valid && encode_warmed_voiced_frame(boost_encoder, boosted);
    valid = valid && encode_warmed_voiced_frame(reduce_encoder, reduced);

    opendmr_encoder_destroy(encoder);
    opendmr_encoder_destroy(zero_encoder);
    opendmr_encoder_destroy(boost_encoder);
    opendmr_encoder_destroy(reduce_encoder);

    valid = valid && expect_frame("unexpected warmed voiced AMBE frame", initial, expected);
    valid = valid && expect_frame("encoder reset changed initial state", after_reset, expected);
    valid = valid && expect_frame("explicit 0 dB changed the DMR default", explicit_zero, expected);
    valid = valid && std::memcmp(boosted, initial, sizeof(initial)) != 0;
    valid = valid && std::memcmp(reduced, initial, sizeof(initial)) != 0;
    valid = valid && std::memcmp(boosted, reduced, sizeof(initial)) != 0;
    if (!valid)
        std::fputs("gain adjustment did not produce distinct vectors\n", stderr);
    return valid;
}

int main()
{
    if (std::strcmp(opendmr_version(), "1.0.0+dmroute.2") != 0) {
        std::fprintf(stderr, "unexpected OpenDMR version: %s\n", opendmr_version());
        return 1;
    }
    if (!encode_silence_vector()) {
        std::fputs("silence vector failed\n", stderr);
        return 1;
    }
    if (!encode_voiced_and_gain_vectors()) {
        std::fputs("voiced/gain vector failed\n", stderr);
        return 1;
    }

    std::puts("OpenDMR encoder regression tests passed");
    return 0;
}
