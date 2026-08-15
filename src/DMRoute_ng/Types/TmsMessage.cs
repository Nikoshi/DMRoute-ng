namespace DMRoute_ng.Types;

public ref struct TmsMessage
{
    public bool IsValid { get; }
    public byte EncodingByte { get; }
    public ReadOnlySpan<byte> TextBytes { get; }

    public TmsMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            IsValid = false;
            EncodingByte = 0;
            TextBytes = default;
            return;
        }

        int headerLength = 0;

        // Command-Byte (0x80+) gibt den Nachrichtentyp an
        // Weiche für Unconfirmed Data (oft 6 Bytes Header)
        if ((data[4] & 0x80) != 0)
        {
            headerLength = 6;
            EncodingByte = data[5];
        }
        // Weiche für Confirmed Data (oft 8 Bytes Header)
        else if (data.Length >= 8 && (data[6] & 0x80) != 0)
        {
            headerLength = 8;
            EncodingByte = data[7];
        }
        else
        {
            IsValid = false;
            EncodingByte = 0;
            TextBytes = default;
            return;
        }

        IsValid = true;
        TextBytes = data.Slice(headerLength);
    }
}