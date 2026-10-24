namespace DMRoute_ng.Emulator;

public readonly record struct DmrdFrameInfo(
    byte SequenceNumber,
    int SourceId,
    int DestinationId,
    int RepeaterId,
    int Timeslot,
    bool IsGroupCall,
    bool IsDataFrame,
    byte DataType,
    uint StreamId);
