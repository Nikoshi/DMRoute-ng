using DMRoute_ng.Core;
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
    
    // Konfiguration für deinen Pi-Star anlegen
    var repeaterConfig = new RepeaterConfiguration(
        "M1ABC",
        "446.00625",
        "446.00625",
        0,
        1,
        0,
        0,
        0,
        "",
        "",
        "",
        "",
        ""
    );
    
    var hotspot = new Repeater(1000001, "s3cr37w0rd", RepeaterState.Disconnected, repeaterConfig);

    registry.AddOrUpdate(hotspot);
    return registry;
});

builder.Services.AddSingleton<MicroSubnetRouter>();

// UDP-Server starten
builder.Services.AddHostedService<DmrServer>();

var host = builder.Build();
host.Run();