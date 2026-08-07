using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using DMRoute_ng.Types;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Registry registrieren und Test-Repeater "whitelisten"
builder.Services.AddSingleton<RepeaterRegistry>(sp => 
{
    var registry = new RepeaterRegistry();
    var hotspot = new Repeater(1000001, "s3cr37w0rd", RepeaterState.Disconnected, null);

    registry.AddOrUpdate(hotspot);
    return registry;
});

const int myZoneId = 100;
builder.Services.AddSingleton(sp => 
    new MicroSubnetRouter(
        sp.GetRequiredService<ILogger<MicroSubnetRouter>>(), 
        sp.GetRequiredService<RepeaterRegistry>(), 
        myZoneId
    )
);

// UDP-Server starten
builder.Services.AddHostedService<DmrServer>();
builder.Services.AddSingleton<SdsGateway>();

var host = builder.Build();

// Erzwingt die Instanziierung des Gateways beim Start, damit das Event abonniert wird
host.Services.GetRequiredService<SdsGateway>();

host.Run();