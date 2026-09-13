namespace DMRoute_ng.Emulator;

public sealed record HomebrewDatagram(byte[] Payload, DateTimeOffset ReceivedAt);
