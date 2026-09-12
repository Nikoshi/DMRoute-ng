using System.Buffers.Text;

namespace DMRoute_ng.Integration;

public ref struct JsonSpanBuilder
{
    private readonly Span<byte> _buffer;
    private int _offset;
    private bool _firstElement;
    private readonly bool _isArrayRoot;

    public JsonSpanBuilder(Span<byte> buffer, bool isArrayRoot = false)
    {
        _buffer = buffer;
        _offset = 0;
        _firstElement = true;
        _isArrayRoot = isArrayRoot;
        WriteByte((byte)(isArrayRoot ? '[' : '{'));
    }

    public readonly int Length => _offset;

    private void WriteByte(byte b) => _buffer[_offset++] = b;

    private void WriteComma()
    {
        if (!_firstElement) WriteByte((byte)',');
        else _firstElement = false;
    }

    // Neu: Startet ein neues Objekt innerhalb eines Arrays
    public void StartArrayObject()
    {
        WriteComma();
        WriteByte((byte)'{');
        _firstElement = true; // Reset für die inneren Keys
    }

    // Neu: Beendet das aktuelle Objekt im Array
    public void EndArrayObject()
    {
        WriteByte((byte)'}');
        _firstElement = false; 
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
    
    public void AppendString(ReadOnlySpan<byte> key, string? value)
    {
        AppendKey(key);
        WriteByte((byte)'"');
        if (!string.IsNullOrEmpty(value))
        {
            AppendEscapedString(value);
        }
        WriteByte((byte)'"');
    }

    private void AppendEscapedString(ReadOnlySpan<char> value)
    {
        var segmentStart = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is not ('"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t') && c >= 0x20) continue;

            if (i > segmentStart)
                _offset += System.Text.Encoding.UTF8.GetBytes(value[segmentStart..i], _buffer[_offset..]);

            WriteByte((byte)'\\');
            switch (c)
            {
                case '"': WriteByte((byte)'"'); break;
                case '\\': WriteByte((byte)'\\'); break;
                case '\b': WriteByte((byte)'b'); break;
                case '\f': WriteByte((byte)'f'); break;
                case '\n': WriteByte((byte)'n'); break;
                case '\r': WriteByte((byte)'r'); break;
                case '\t': WriteByte((byte)'t'); break;
                default:
                    WriteByte((byte)'u');
                    WriteByte((byte)'0');
                    WriteByte((byte)'0');
                    WriteHexNibble(c >> 4);
                    WriteHexNibble(c);
                    break;
            }
            segmentStart = i + 1;
        }

        if (segmentStart < value.Length)
            _offset += System.Text.Encoding.UTF8.GetBytes(value[segmentStart..], _buffer[_offset..]);
    }

    private void WriteHexNibble(int value)
    {
        value &= 0x0F;
        WriteByte((byte)(value < 10 ? '0' + value : 'A' + value - 10));
    }
    
    // Optional, aber nützlich für bools
    public void AppendBool(ReadOnlySpan<byte> key, bool value)
    {
        AppendKey(key);
        ReadOnlySpan<byte> valSpan = value ? "true"u8 : "false"u8;
        valSpan.CopyTo(_buffer.Slice(_offset));
        _offset += valSpan.Length;
    }
    
    public void Finish()
    {
        WriteByte((byte)(_isArrayRoot ? ']' : '}'));
    }

    
}
