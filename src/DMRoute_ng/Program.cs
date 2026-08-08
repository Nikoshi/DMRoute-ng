using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Debug);

var myZoneId = builder.Configuration.GetValue("ZoneId", 100);
var meshPsk = builder.Configuration.GetValue<string>("MeshPsk", "s3cr37m3sh");
var myZonePsk = builder.Configuration.GetValue<string>("ZonePsk", "s3cr37w0rd");


builder.Services.AddSingleton<MasterRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterRegistry>());
builder.Services.AddSingleton<RepeaterRegistry>(sp => 
    new RepeaterRegistry(sp.GetRequiredService<ILogger<RepeaterRegistry>>(), myZoneId, myZonePsk)
);

// Als HostedService registrieren, damit das Housekeeping läuft
builder.Services.AddHostedService(sp => sp.GetRequiredService<RepeaterRegistry>());

builder.Services.AddSingleton(sp => 
    new MicroSubnetRouter(
        sp.GetRequiredService<ILogger<MicroSubnetRouter>>(), 
        sp.GetRequiredService<RepeaterRegistry>(), 
        sp.GetRequiredService<MasterRegistry>(), // Neue Abhängigkeit
        myZoneId
    )
);

builder.Services.AddHostedService(sp => new MeshDiscoveryService(
    sp.GetRequiredService<ILogger<MeshDiscoveryService>>(),
    sp.GetRequiredService<MasterRegistry>(),
    myZoneId: myZoneId,
    myDataPort: 62031, // Port, auf dem DmrServer lauscht
    discoveryPort: 42069, // Konfigurierbarer Mesh-Port
    meshPsk: meshPsk
));

// UDP-Server starten
builder.Services.AddHostedService<DmrServer>();
builder.Services.AddSingleton<SdsGateway>();

var host = builder.Build();

// Erzwingt die Instanziierung des Gateways beim Start, damit das Event abonniert wird
host.Services.GetRequiredService<SdsGateway>();

host.Run();