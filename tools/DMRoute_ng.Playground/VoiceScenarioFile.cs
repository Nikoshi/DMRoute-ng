using System.Globalization;
using System.Text;
using DMRoute_ng.Coding;
using DMRoute_ng.Emulator;

namespace DMRoute_ng.Playground;

internal static class VoiceScenarioFile
{
    private const int MaxScenarioSteps = 10_000;

    public static async Task WriteAsync(
        string path,
        PlaygroundRadioSettings radio,
        int repeaterId,
        int destinationId,
        bool isGroupCall,
        ReadOnlyMemory<byte> ambe,
        CancellationToken cancellationToken = default)
    {
        if (ambe.Length == 0 || ambe.Length % DmrVoiceBurstCodec.AmbePayloadLength != 0)
            throw new ArgumentException("AMBE muss vollständige 60-ms-Sprachbursts enthalten.", nameof(ambe));
        var voiceBursts = ambe.Length / DmrVoiceBurstCodec.AmbePayloadLength;
        if (voiceBursts + 5 > MaxScenarioSteps)
            throw new ArgumentException($"Der Ruf überschreitet die Grenze von {MaxScenarioSteps} Szenarioschritten.", nameof(ambe));

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync($"name=Sprache {radio.RadioId} nach {destinationId}").ConfigureAwait(false);
        await writer.WriteLineAsync($"radioId={radio.RadioId.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        var streamId = CreateStreamId(radio.RadioId, destinationId, ambe.Span);
        byte sequence = 0;
        var packet = new byte[DmrBurstWriter.DmrdLength];
        var burst = new byte[DmrBurstWriter.BurstLength];

        for (var header = 0; header < 3; header++)
        {
            DmrVoiceBurstWriter.WriteLinkControl(burst, radio.RadioId, destinationId, isGroupCall, false);
            DmrBurstWriter.WriteDmrd(packet, burst, sequence++, radio.RadioId, destinationId, repeaterId,
                1, streamId, radio.Timeslot, isGroupCall);
            await WriteStepAsync(writer, header == 0 ? 0 : 60, packet).ConfigureAwait(false);
        }
        for (var index = 0; index < voiceBursts; index++)
        {
            var voiceIndex = (byte)(index % 6);
            DmrVoiceBurstWriter.WriteVoice(burst,
                ambe.Span.Slice(index * DmrVoiceBurstCodec.AmbePayloadLength, DmrVoiceBurstCodec.AmbePayloadLength),
                radio.RadioId, destinationId, isGroupCall, voiceIndex);
            DmrBurstWriter.WriteVoiceDmrd(packet, burst, sequence++, radio.RadioId, destinationId, repeaterId,
                streamId, voiceIndex, radio.Timeslot, isGroupCall);
            await WriteStepAsync(writer, 60, packet).ConfigureAwait(false);
        }
        for (var terminator = 0; terminator < 2; terminator++)
        {
            DmrVoiceBurstWriter.WriteLinkControl(burst, radio.RadioId, destinationId, isGroupCall, true);
            DmrBurstWriter.WriteDmrd(packet, burst, sequence++, radio.RadioId, destinationId, repeaterId,
                2, streamId, radio.Timeslot, isGroupCall);
            await WriteStepAsync(writer, 60, packet).ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static byte[] ReadAmbe(string path)
    {
        var loaded = ScenarioFile.Load(path);
        var output = new List<byte>();
        DmrdFrameInfo? call = null;
        byte? previousSequence = null;
        var headerSeen = false;
        var voiceSeen = false;
        var terminatorSeen = false;
        byte expectedVoiceIndex = 0;
        Span<byte> payload = stackalloc byte[DmrVoiceBurstCodec.AmbePayloadLength];

        foreach (var step in loaded.Scenario.Steps)
        {
            var packet = step.Packet.Span;
            if (!HomebrewProtocol.TryReadDmrdInfo(packet, out var info) || packet.Length < DmrBurstWriter.DmrdLength)
                throw new ScenarioFormatException("Der Sprachruf enthält ein unvollständiges DMRD-Datagramm.");
            if (previousSequence.HasValue && info.SequenceNumber != unchecked((byte)(previousSequence.Value + 1)))
                throw new ScenarioFormatException("Der Sprachruf enthält eine Lücke oder vertauschte Sequenznummern.");
            previousSequence = info.SequenceNumber;

            if (call.HasValue && !SameCall(call.Value, info))
                throw new ScenarioFormatException("Die Datei enthält mehrere oder widersprüchliche Sprachströme.");
            call ??= info;

            if (info.IsDataFrame)
            {
                if (info.DataType == 0)
                    throw new ScenarioFormatException("Verschlüsselte DMR-Sprachrufe werden nicht unterstützt.");
                if (info.DataType is 1 or 2)
                {
                    var isTerminator = info.DataType == 2;
                    if (!DmrVoiceBurstWriter.TryReadLinkControl(packet.Slice(20, 33), isTerminator,
                            out var lcSource, out var lcDestination, out var lcGroup) ||
                        lcSource != info.SourceId || lcDestination != info.DestinationId || lcGroup != info.IsGroupCall)
                        throw new ScenarioFormatException("Link Control und DMRD-Metadaten widersprechen sich.");
                    if (!isTerminator && !voiceSeen && !terminatorSeen) { headerSeen = true; continue; }
                    if (isTerminator && headerSeen && voiceSeen) { terminatorSeen = true; continue; }
                }
                throw new ScenarioFormatException("Der Sprachruf enthält unerwartete Datenbursts.");
            }
            if (!headerSeen || terminatorSeen)
                throw new ScenarioFormatException("Der Sprachruf besitzt keine vollständige Header-/Terminator-Reihenfolge.");
            if (info.DataType != expectedVoiceIndex)
                throw new ScenarioFormatException("Der Sprachruf enthält eine Lücke oder vertauschte Sprachbursts.");
            if (info.DataType != 0 && DmrVoiceBurstWriter.TryReadPrivacyIndicator(packet.Slice(20, 33), out var privacy) && privacy)
                throw new ScenarioFormatException("Verschlüsselte DMR-Sprachrufe werden nicht unterstützt.");

            DmrVoiceBurstCodec.ReadAmbePayload(packet.Slice(20, 33), payload);
            for (var index = 0; index < payload.Length; index++) output.Add(payload[index]);
            expectedVoiceIndex = (byte)((expectedVoiceIndex + 1) % 6);
            voiceSeen = true;
        }
        if (!headerSeen || !voiceSeen || !terminatorSeen)
            throw new ScenarioFormatException("Die Datei enthält keinen vollständigen unverschlüsselten Sprachruf.");
        return output.ToArray();
    }

    private static bool SameCall(DmrdFrameInfo left, DmrdFrameInfo right) =>
        left.SourceId == right.SourceId && left.DestinationId == right.DestinationId &&
        left.RepeaterId == right.RepeaterId && left.Timeslot == right.Timeslot &&
        left.IsGroupCall == right.IsGroupCall && left.StreamId == right.StreamId;

    private static uint CreateStreamId(int sourceId, int destinationId, ReadOnlySpan<byte> ambe)
    {
        var hash = 2166136261U;
        hash = (hash ^ (uint)sourceId) * 16777619U;
        hash = (hash ^ (uint)destinationId) * 16777619U;
        foreach (var value in ambe) hash = (hash ^ value) * 16777619U;
        return hash == 0 ? 1U : hash;
    }

    private static Task WriteStepAsync(StreamWriter writer, int delayMs, ReadOnlySpan<byte> packet) =>
        writer.WriteLineAsync($"{delayMs.ToString(CultureInfo.InvariantCulture)} {Convert.ToHexString(packet)}");
}
