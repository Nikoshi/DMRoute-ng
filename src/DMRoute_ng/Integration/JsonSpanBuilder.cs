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
            int written = System.Text.Encoding.UTF8.GetBytes(value, _buffer.Slice(_offset));
            _offset += written;
        }
        WriteByte((byte)'"');
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