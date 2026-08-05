using System.Net;

namespace DMRoute_ng.Types;

public enum RepeaterState
{
    ChallengeSent,
    LoggedIn,
    Disconnected
}

public record RepeaterConfiguration(
    string Callsign,
    string RxFreq,
    string TxFreq,
    int TxPower,
    int ColorCode,
    float Latitude,
    float Longitude,
    int Height,
    string Location,
    string Description,
    string Url,
    string SoftwareId,
    string PackageId
);

public sealed class Repeater(int id, string psk, RepeaterState state, RepeaterConfiguration? configuration)
{
    public int Id { get; } = id;
    public string PreSharedKey { get; } = psk;
    public RepeaterState State { get; set; } = state;
    public RepeaterConfiguration? Configuration { get; set; } = configuration;
    public uint RandomNumber { get; set; }
    public DateTime? LastPing { get; set; }
    public IPEndPoint? EndPoint { get; set; }
    
    // Füge dies in die Klasse Repeater ein
    private volatile int[] _talkgroups = [];
    
     
    // Thread-sicheres Hinzufügen (Copy-On-Write) - wird bei PTT aufgerufen
    public void AddTalkgroup(int tg)
    {
        var current = _talkgroups;
        if (Array.IndexOf(current, tg) != -1) return;
        
        var newTgs = new int[current.Length + 1];
        Array.Copy(current, newTgs, current.Length);
        newTgs[current.Length] = tg;
        _talkgroups = newTgs; // Atomarer Pointer-Swap
    }
    
    // Für den Hot-Path: Zero-Allocation Lesezugriff
    public ReadOnlySpan<int> GetTalkgroups() => _talkgroups;
}