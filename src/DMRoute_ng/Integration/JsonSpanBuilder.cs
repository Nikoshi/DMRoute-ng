using System.Buffers.Text;

namespace DMRoute_ng.Integration;

public ref struct JsonSpanBuilder
{
    private readonly Span<byte> _buffer;
    private int _offset;
    private bool _firstElement;

    public JsonSpanBuilder(Span<byte> buffer)
    {
        _buffer = buffer;
        _offset = 0;
        _firstElement = true;
        WriteByte((byte)'{');
    }

    public readonly int Length => _offset;

    private void WriteByte(byte b)
    {
        _buffer[_offset++] = b;
    }

    private void WriteComma()
    {
        if (!_firstElement) WriteByte((byte)',');
        else _firstElement = false;
    }

    private void AppendKey(ReadOnlySpan<byte> key)
    {
        WriteComma();
        WriteByte((byte)'"');
        key.CopyTo(_buffer.Slice(_offset));
        _offset += key.Length;
        WriteByte((byte)'"');
        WriteByte((byte)':');
    }

    public void AppendNumber(ReadOnlySpan<byte> key, long value)
    {
        AppendKey(key);
        Utf8Formatter.TryFormat(value, _buffer.Slice(_offset), out int written);
        _offset += written;
    }

    public void AppendString(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        AppendKey(key);
        WriteByte((byte)'"');
        value.CopyTo(_buffer.Slice(_offset));
        _offset += value.Length;
        WriteByte((byte)'"');
    }

    public void Finish()
    {
        WriteByte((byte)'}');
    }
}