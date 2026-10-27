using System.Buffers.Binary;

namespace DMRoute_ng.Audio;

/// <summary>Liest und schreibt unkomprimiertes, einkanaliges PCM mit 8 kHz und 16 Bit.</summary>
public static class WavePcm16
{
    public const int SampleRate = 8000;
    public const int BytesPerSample = 2;
    private const int HeaderLength = 44;

    public static async Task<short[]> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (data.Length < HeaderLength || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Die Eingabe ist keine gültige WAV-Datei.");

        var formatOffset = FindChunk(data, "fmt "u8);
        var dataOffset = FindChunk(data, "data"u8);
        if (formatOffset < 0 || dataOffset < 0)
            throw new InvalidDataException("Die WAV-Datei enthält keinen gültigen fmt- oder data-Block.");

        var formatLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(formatOffset + 4, 4));
        if (formatLength < 16 || formatOffset + 8 + formatLength > data.Length)
            throw new InvalidDataException("Der fmt-Block der WAV-Datei ist beschädigt.");
        var format = data.AsSpan(formatOffset + 8, formatLength);
        if (BinaryPrimitives.ReadUInt16LittleEndian(format) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) != 1 ||
            BinaryPrimitives.ReadInt32LittleEndian(format[4..]) != SampleRate ||
            BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) != 16)
            throw new InvalidDataException("Unterstützt wird ausschließlich PCM, mono, 8000 Hz und 16 Bit.");

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(dataOffset + 4, 4));
        var payloadOffset = dataOffset + 8;
        if (payloadLength < 0 || (payloadLength & 1) != 0 || payloadOffset + payloadLength > data.Length)
            throw new InvalidDataException("Der data-Block der WAV-Datei ist beschädigt.");

        var samples = new short[payloadLength / BytesPerSample];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(payloadOffset + index * 2, 2));
        return samples;
    }

    public static async Task WriteAsync(
        string path, ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        var payloadLength = checked(samples.Length * BytesPerSample);
        var data = new byte[HeaderLength + payloadLength];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), data.Length - 8);
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), SampleRate * BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32, 2), BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34, 2), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40, 4), payloadLength);
        for (var index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(HeaderLength + index * 2, 2), samples.Span[index]);
        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    private static int FindChunk(ReadOnlySpan<byte> data, ReadOnlySpan<byte> id)
    {
        var offset = 12;
        while (offset <= data.Length - 8)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
            if (length < 0 || offset + 8L + length > data.Length) return -1;
            if (data.Slice(offset, 4).SequenceEqual(id)) return offset;
            offset = checked(offset + 8 + length + (length & 1));
        }
        return -1;
    }
}
