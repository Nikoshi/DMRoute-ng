# DMRoute-ng-Emulatorkern

`DMRoute_ng.Emulator` ist eine eigenständige .NET-Bibliothek für automatisierte
Homebrew-Protokollszenarien. Sie hängt weder von der DMRoute-ng-Serverassembly
noch von einem Testframework ab. Eine spätere CLI oder TUI kann deshalb dieselbe
API verwenden.

Die erste Version unterstützt die vollständige Hotspot-Anmeldung (`RPTL`,
Challenge, `RPTK`, `RPTC`), manuelles oder periodisches Keepalive, Abmeldung,
DMRD-Wiedergabe und die begrenzte Aufzeichnung der vom Master zurückgesendeten
DMRD-Pakete.

```csharp
var hotspot = new VirtualHotspot(new VirtualHotspotOptions
{
    MasterEndPoint = new IPEndPoint(IPAddress.Loopback, 62031),
    RepeaterId = 1000001,
    PreSharedKey = "zone-secret1000001"
});

await hotspot.LoginAsync();
await hotspot.PingAsync();
await hotspot.SendDmrdAsync(frame);
var response = await hotspot.ReceiveDmrdAsync();
await hotspot.DisconnectAsync();
await hotspot.DisposeAsync();
```

`RadioScenario` übernimmt den Besitz der übergebenen Frames und bewahrt ihre
relativen Zeitabstände. `VirtualRadio` prüft vor der Wiedergabe über einen
konfigurierten Hotspot die Funkgerät-ID. Die Frame-Erzeugung aus semantischen
Sprach-, CSBK- oder SDS-Eingaben und ein interaktives Frontend sind bewusst als
getrennte Folgefunktionen geplant.
