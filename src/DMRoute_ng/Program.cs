using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Integration; // NEU
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Fail-Fast: MQTT Konfiguration prüfen
var mqttHost = builder.Configuration.GetValue<string>("Mqtt:Host");
if (string.IsNullOrWhiteSpace(mqttHost))
{
    Console.Error.WriteLine("FATAL ERROR: MQTT Broker Host (Mqtt:Host) ist nicht konfiguriert! Beende...");
    Environment.Exit(1);
}

var myZoneId = builder.Configuration.GetValue("ZoneId", 100);
var meshPsk = builder.Configuration.GetValue<string>("MeshPsk", "s3cr37m3sh");
var myZonePsk = builder.Configuration.GetValue<string>("ZonePsk", "s3cr37w0rd");

// --- Registries & Background Tasks ---
builder.Services.AddSingleton<MasterRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterRegistry>());

builder.Services.AddSingleton<RoamingRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RoamingRegistry>());

builder.Services.AddSingleton<RepeaterRegistry>(sp => 
    new RepeaterRegistry(sp.GetRequiredService<ILogger<RepeaterRegistry>>(), myZoneId, myZonePsk)
);
builder.Services.AddHostedService(sp => sp.GetRequiredService<RepeaterRegistry>());

// --- Services & Router ---
builder.Services.AddSingleton(sp => new MeshDiscoveryService(
    sp.GetRequiredService<ILogger<MeshDiscoveryService>>(),
    sp.GetRequiredService<MasterRegistry>(),
    sp.GetRequiredService<RoamingRegistry>(),
    myZoneId: myZoneId,
    myDataPort: 62031,
    discoveryPort: 42069, 
    meshPsk: meshPsk
));
builder.Services.AddHostedService(sp => sp.GetRequiredService<MeshDiscoveryService>());

builder.Services.AddSingleton(sp => 
    new MicroSubnetRouter(
        sp.GetRequiredService<ILogger<MicroSubnetRouter>>(), 
        sp.GetRequiredService<RepeaterRegistry>(), 
        sp.GetRequiredService<MasterRegistry>(),
        sp.GetRequiredService<RoamingRegistry>(),
        sp.GetRequiredService<MeshDiscoveryService>(),
        myZoneId
    )
);

builder.Services.AddHostedService<DmrServer>();
builder.Services.AddSingleton<SdsGateway>();

// NEU: MQTT Integration Service registrieren
builder.Services.AddHostedService<MqttIntegrationService>();

var host = builder.Build();
host.Services.GetRequiredService<SdsGateway>();

host.Run();