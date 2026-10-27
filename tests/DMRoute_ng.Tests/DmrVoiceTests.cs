using DMRoute_ng.Audio;
using DMRoute_ng.Coding;
using DMRoute_ng.Emulator;
using DMRoute_ng.Playground;

namespace DMRoute_ng.Tests;

public sealed class DmrVoiceTests
{
    [Fact]
    public void VoiceBurstCodec_RoundTripsThreeCanonicalAmbeFrames()
    {
        Span<byte> input = stackalloc byte[DmrVoiceBurstCodec.AmbePayloadLength];
        for (var index = 0; index < input.Length; index++) input[index] = (byte)(index * 37 + 11);
        Span<byte> burst = stackalloc byte[DmrVoiceBurstCodec.BurstLength];
        burst.Fill(0xA5);
        DmrVoiceBurstCodec.WriteAmbePayload(burst, input);
        Span<byte> output = stackalloc byte[DmrVoiceBurstCodec.AmbePayloadLength];
        DmrVoiceBurstCodec.ReadAmbePayload(burst, output);

        Assert.True(input.SequenceEqual(output));
        Assert.Equal(0x05, burst[13] & 0x0F);
        Assert.True(burst.Slice(14, 5).SequenceEqual(stackalloc byte[] { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 }));
        Assert.Equal(0xA0, burst[19] & 0xF0);
    }

    [Fact]
    public void VoiceBurstCodec_WarmedRoundTripAllocatesNothing()
    {
        Span<byte> ambe = stackalloc byte[DmrVoiceBurstCodec.AmbePayloadLength];
        Span<byte> burst = stackalloc byte[DmrVoiceBurstCodec.BurstLength];
        DmrVoiceBurstWriter.WriteVoice(burst, ambe, 10001, 9, true, 1);
        DmrVoiceBurstCodec.ReadAmbePayload(burst, ambe);
        DmrVoiceBurstWriter.WriteLinkControl(burst, 10001, 9, true, false);
        Assert.True(DmrVoiceBurstWriter.TryReadLinkControl(burst, false, out _, out _, out _));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            DmrVoiceBurstWriter.WriteVoice(burst, ambe, 10001, 9, true, (byte)(index % 6));
            DmrVoiceBurstCodec.ReadAmbePayload(burst, ambe);
            DmrVoiceBurstWriter.WriteLinkControl(burst, 10001, 9, true, false);
            DmrVoiceBurstWriter.TryReadLinkControl(burst, false, out _, out _, out _);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LinkControl_RoundTripsIdsCallTypeAndTerminatorMask(bool group, bool terminator)
    {
        Span<byte> burst = stackalloc byte[DmrVoiceBurstCodec.BurstLength];
        DmrVoiceBurstWriter.WriteLinkControl(burst, 10001, 10101, group, terminator);

        Assert.True(DmrVoiceBurstWriter.TryReadLinkControl(
            burst, terminator, out var sourceId, out var destinationId, out var isGroupCall));
        Assert.Equal(10001, sourceId);
        Assert.Equal(10101, destinationId);
        Assert.Equal(group, isGroupCall);
        Assert.False(DmrVoiceBurstWriter.TryReadLinkControl(
            burst, !terminator, out _, out _, out _));
    }

    [Fact]
    public async Task WavePcm16_RoundTripsSupportedFormat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmroute-wave-{Guid.NewGuid():N}.wav");
        try
        {
            short[] samples = [-32768, -1, 0, 1, 32767];
            await WavePcm16.WriteAsync(path, samples);
            Assert.Equal(samples, await WavePcm16.ReadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VoiceScenario_RoundTripsAmbeAndMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dmroute-voice-{Guid.NewGuid():N}.dmrd");
        try
        {
            var radio = new PlaygroundRadioSettings { RadioId = 10001, Timeslot = 2 };
            var ambe = new byte[DmrVoiceBurstCodec.AmbePayloadLength * 2];
            for (var index = 0; index < ambe.Length; index++) ambe[index] = (byte)(index * 13);
            await VoiceScenarioFile.WriteAsync(path, radio, 1000001, 9, true, ambe);

            Assert.Equal(ambe, VoiceScenarioFile.ReadAmbe(path));
            var loaded = ScenarioFile.Load(path);
            Assert.Equal(7, loaded.Scenario.Steps.Count);
            Assert.All(loaded.Scenario.Steps,
                step => Assert.Equal(10001, HomebrewProtocol.ReadDmrdSourceId(step.Packet.Span)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
