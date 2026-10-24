using System.Buffers.Binary;
using System.Buffers.Text;
using System.Threading.Channels;
using DMRoute_ng.Coding;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Services;

public sealed partial class SdsEchoService
{
    private static int BuildReplyText(RequestSlot slot, Span<byte> destination)
    {
        var offset = 0;
        if (slot.TooLong)
            AppendAscii("ERROR text too long (max 120) | RX "u8, destination, ref offset);
        else
        {
            AppendAscii("ECHO "u8, destination, ref offset);
            slot.Text.AsSpan(0, slot.TextLength).CopyTo(destination[offset..]);
            offset += slot.TextLength;
            AppendAscii(" | RX "u8, destination, ref offset);
        }

        Span<byte> duration = stackalloc byte[20];
        var milliseconds = Math.Max(0, slot.ReceiveDurationTicks / TimeSpan.TicksPerMillisecond);
        Utf8Formatter.TryFormat(milliseconds, duration, out var durationLength);
        AppendAscii(duration[..durationLength], destination, ref offset);
        AppendAscii(" ms"u8, destination, ref offset);
        return offset;
    }

    private static void AppendAscii(ReadOnlySpan<byte> ascii, Span<byte> destination, ref int offset)
    {
        foreach (var value in ascii)
        {
            destination[offset++] = value;
            destination[offset++] = 0;
        }
    }

    private static bool StartsWithEcho(ReadOnlySpan<byte> text) =>
        text.Length >= 10 && text[0] == (byte)'E' && text[1] == 0 &&
        text[2] == (byte)'C' && text[3] == 0 && text[4] == (byte)'H' && text[5] == 0 &&
        text[6] == (byte)'O' && text[7] == 0 && text[8] == (byte)' ' && text[9] == 0;

    private static ReadOnlySpan<byte> TrimTransportWhitespace(ReadOnlySpan<byte> text)
    {
        if ((text.Length & 1) != 0) text = text[..^1];
        while (text.Length >= 2 && ReadCodeUnit(text) is '\0' or '\r' or '\n') text = text[2..];
        while (text.Length >= 2 && ReadCodeUnit(text[^2..]) is '\0' or '\r' or '\n') text = text[..^2];
        return text;
    }

    private static bool IsWhiteSpace(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty) return true;
        for (var offset = 0; offset + 1 < text.Length; offset += 2)
        {
            if (ReadCodeUnit(text[offset..]) is not ('\0' or ' ' or '\t' or '\r' or '\n')) return false;
        }
        return true;
    }

    private static char ReadCodeUnit(ReadOnlySpan<byte> text) => (char)(text[0] | (text[1] << 8));

    private static ulong Hash(ReadOnlySpan<byte> text)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in text) hash = (hash ^ value) * prime;
        return hash;
    }

    private bool IsDuplicate(int sourceId, int textLength, ulong hash, long now)
    {
        var cutoff = now - DuplicateWindow.Ticks;
        for (var i = 0; i < _duplicates.Length; i++)
        {
            ref var entry = ref _duplicates[i];
            if (entry.SeenAtTicks >= cutoff && entry.SourceId == sourceId &&
                entry.TextLength == textLength && entry.Hash == hash) return true;
        }
        return false;
    }

    private void Remember(int sourceId, int textLength, ulong hash, long now)
    {
        ref var entry = ref _duplicates[_nextDuplicateSlot];
        entry = new DuplicateEntry { SourceId = sourceId, TextLength = textLength, Hash = hash, SeenAtTicks = now };
        _nextDuplicateSlot = (_nextDuplicateSlot + 1) % _duplicates.Length;
    }

    private int RentSlot()
    {
        for (var i = 0; i < _slots.Length; i++) if (_slots[i].TryRent()) return i;
        return -1;
    }
}
