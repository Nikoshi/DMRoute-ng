using System.Net;
using System.Text.Json.Serialization;
using DMRoute_ng.Emulator;

namespace DMRoute_ng.Playground;

internal sealed record PlaygroundSettings
{
    public PlaygroundMasterSettings Master { get; init; } = new();
    public PlaygroundHotspotSettings[] Hotspots { get; init; } = [];
    public PlaygroundRadioSettings[] Radios { get; init; } = [];
    public PlaygroundCodecSettings? Codec { get; init; } = new();

    public void Validate()
    {
        if (Master is null)
            throw new PlaygroundConfigurationException("Master darf nicht fehlen.");
        if (Hotspots is null)
            throw new PlaygroundConfigurationException("Hotspots darf nicht fehlen.");
        if (Radios is null)
            throw new PlaygroundConfigurationException("Radios darf nicht fehlen.");
        Master.Validate();
        if (Hotspots.Length == 0)
            throw new PlaygroundConfigurationException("Mindestens ein Hotspot muss konfiguriert sein.");
        if (Hotspots.Length > 64)
            throw new PlaygroundConfigurationException("Höchstens 64 Hotspots dürfen konfiguriert sein.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        foreach (var hotspot in Hotspots)
        {
            if (hotspot is null)
                throw new PlaygroundConfigurationException("Hotspots darf keine leeren Einträge enthalten.");
            hotspot.Validate();
            if (!names.Add(hotspot.Name))
                throw new PlaygroundConfigurationException($"Der Hotspotname '{hotspot.Name}' ist doppelt vorhanden.");
            if (!ids.Add(hotspot.RepeaterId))
                throw new PlaygroundConfigurationException($"Die Repeater-ID {hotspot.RepeaterId} ist doppelt vorhanden.");
        }

        var radioNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var radioIds = new HashSet<int>();
        foreach (var radio in Radios)
        {
            if (radio is null)
                throw new PlaygroundConfigurationException("Radios darf keine leeren Einträge enthalten.");
            radio.Validate();
            if (!radioNames.Add(radio.Name))
                throw new PlaygroundConfigurationException($"Der Funkgerätname '{radio.Name}' ist doppelt vorhanden.");
            if (!radioIds.Add(radio.RadioId))
                throw new PlaygroundConfigurationException($"Die Funkgerät-ID {radio.RadioId} ist doppelt vorhanden.");
            if (!names.Contains(radio.Hotspot))
                throw new PlaygroundConfigurationException(
                    $"Funkgerät '{radio.Name}' verweist auf den unbekannten Hotspot '{radio.Hotspot}'.");
        }
    }

    public async Task<IPEndPoint> ResolveMasterAsync(CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(Master.Host, out var parsed))
        {
            if (parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new PlaygroundConfigurationException("Der Master muss eine IPv4-Adresse verwenden.");
            return new IPEndPoint(parsed, Master.Port);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(Master.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new PlaygroundConfigurationException(
                $"Der Master-Hostname '{Master.Host}' konnte nicht aufgelöst werden.", exception);
        }

        foreach (var address in addresses)
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return new IPEndPoint(address, Master.Port);
        }

        throw new PlaygroundConfigurationException(
            $"Der Master-Hostname '{Master.Host}' besitzt keine IPv4-Adresse.");
    }
}

internal sealed record PlaygroundCodecSettings
{
    public string? OpenDmrExecutable { get; init; }
    public string? EncoderExecutable { get; init; }
}

internal sealed record PlaygroundRadioSettings
{
    public string Name { get; init; } = "radio1";
    public int RadioId { get; init; } = 10001;
    public string Hotspot { get; init; } = "hs1";
    public int Timeslot { get; init; } = 2;
    public int[] Talkgroups { get; init; } = [9];
    public int InboxCapacity { get; init; } = 128;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Name.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            throw new PlaygroundConfigurationException(
                "Funkgerätenamen dürfen nur Buchstaben, Ziffern, '-' und '_' enthalten.");
        if (RadioId is < 10000 or > 99999)
            throw new PlaygroundConfigurationException($"Funkgerät '{Name}': RadioId muss fünfstellig sein.");
        if (string.IsNullOrWhiteSpace(Hotspot))
            throw new PlaygroundConfigurationException($"Funkgerät '{Name}': Hotspot darf nicht leer sein.");
        if (Timeslot is not (1 or 2))
            throw new PlaygroundConfigurationException($"Funkgerät '{Name}': Timeslot muss 1 oder 2 sein.");
        if (Talkgroups is null || Talkgroups.Any(static id => id is <= 0 or > 0xFFFFFF) ||
            Talkgroups.Distinct().Count() != Talkgroups.Length)
            throw new PlaygroundConfigurationException($"Funkgerät '{Name}': Talkgroups müssen eindeutig und gültig sein.");
        if (InboxCapacity <= 0)
            throw new PlaygroundConfigurationException($"Funkgerät '{Name}': InboxCapacity muss positiv sein.");
    }

    public VirtualRadioOptions ToOptions(int colorCode) => new()
    {
        RadioId = RadioId,
        Timeslot = Timeslot,
        ColorCode = colorCode,
        Talkgroups = Talkgroups,
        InboxCapacity = InboxCapacity
    };
}

internal sealed record PlaygroundMasterSettings
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 62031;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new PlaygroundConfigurationException("Master:Host darf nicht leer sein.");
        if (Port is <= 0 or > 65535)
            throw new PlaygroundConfigurationException("Master:Port muss zwischen 1 und 65535 liegen.");
    }
}

internal sealed record PlaygroundHotspotSettings
{
    public string Name { get; init; } = "hs1";
    public int RepeaterId { get; init; } = 1000001;
    public string PreSharedKey { get; init; } = string.Empty;
    public int KeepaliveSeconds { get; init; } = 10;
    public int OperationTimeoutSeconds { get; init; } = 2;
    public int ReceiveQueueCapacity { get; init; } = 256;
    public HotspotConfiguration Configuration { get; init; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Name.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            throw new PlaygroundConfigurationException(
                "Hotspotnamen dürfen nur Buchstaben, Ziffern, '-' und '_' enthalten.");
        if (RepeaterId <= 0)
            throw new PlaygroundConfigurationException($"Hotspot '{Name}': RepeaterId muss positiv sein.");
        if (string.IsNullOrEmpty(PreSharedKey))
            throw new PlaygroundConfigurationException($"Hotspot '{Name}': PreSharedKey darf nicht leer sein.");
        if (KeepaliveSeconds <= 0)
            throw new PlaygroundConfigurationException($"Hotspot '{Name}': KeepaliveSeconds muss positiv sein.");
        if (OperationTimeoutSeconds <= 0)
            throw new PlaygroundConfigurationException($"Hotspot '{Name}': OperationTimeoutSeconds muss positiv sein.");
        if (ReceiveQueueCapacity <= 0)
            throw new PlaygroundConfigurationException($"Hotspot '{Name}': ReceiveQueueCapacity muss positiv sein.");

        try
        {
            _ = HomebrewProtocol.CreateRptk(RepeaterId, 0, PreSharedKey);
            _ = HomebrewProtocol.CreateRptc(RepeaterId, Configuration);
        }
        catch (ArgumentException exception)
        {
            throw new PlaygroundConfigurationException($"Hotspot '{Name}': {exception.Message}", exception);
        }
    }

    public VirtualHotspotOptions ToOptions(IPEndPoint masterEndPoint) => new()
    {
        MasterEndPoint = masterEndPoint,
        RepeaterId = RepeaterId,
        PreSharedKey = PreSharedKey,
        Configuration = Configuration,
        KeepaliveInterval = TimeSpan.FromSeconds(KeepaliveSeconds),
        OperationTimeout = TimeSpan.FromSeconds(OperationTimeoutSeconds),
        ReceiveQueueCapacity = ReceiveQueueCapacity
    };
}

internal sealed class PlaygroundConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PlaygroundSettings))]
internal sealed partial class PlaygroundJsonContext : JsonSerializerContext;
