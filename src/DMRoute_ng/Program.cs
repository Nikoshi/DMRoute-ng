using System.Text;
using DMRoute_ng.Configuration;
using DMRoute_ng.Core;
using DMRoute_ng.Gateways;
using DMRoute_ng.Integration;
using DMRoute_ng.Registry;
using DMRoute_ng.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

return DmRouteApplication.Run(args);

internal static class DmRouteApplication
{
    public static int Run(string[] args)
    {
        if (!StartupArguments.TryParse(args, out var startup, out var argumentError))
        {
            Console.Error.WriteLine($"Configuration error: {argumentError}");
            Console.Error.WriteLine("Usage: DMRoute-ng [--setup] [--config <path>] [configuration arguments]");
            return 2;
        }

        if (startup.Setup) return RunSetup(startup.ConfigPath ?? ConfigurationFile.DefaultPath);

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = startup.ConfigurationArguments
            });
            builder.Logging.SetMinimumLevel(LogLevel.Debug);

            ConfigurationPipeline.ApplyOverrides(builder.Configuration, startup);

            var settings = DmRouteSettings.FromConfiguration(builder.Configuration);
            ConfigureServices(builder, settings);

            using var host = builder.Build();
            host.Services.GetRequiredService<SdsGateway>();
            host.Run();
            return 0;
        }
        catch (Exception exception) when (exception is ConfigurationValidationException or InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Configuration error: {exception.Message}");
            return 2;
        }
    }

    private static int RunSetup(string path)
    {
        try
        {
            var initial = ConfigurationFile.LoadOrDefault(path);
            var result = new SetupWizard(new ConsoleSetupTerminal()).Run(path, initial);
            if (result == SetupResult.Saved)
            {
                Console.WriteLine($"Configuration saved to {Path.GetFullPath(path)}");
                Console.WriteLine($"Start DMRoute-ng with --config \"{path}\".");
            }

            return result == SetupResult.Error ? 2 : 0;
        }
        catch (Exception exception) when (exception is ConfigurationValidationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Setup error: {exception.Message}");
            return 2;
        }
    }

    private static void ConfigureServices(HostApplicationBuilder builder, DmRouteSettings settings)
    {
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<MasterRegistry>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterRegistry>());

        builder.Services.AddSingleton<RoamingRegistry>(sp => new RoamingRegistry(
            sp.GetRequiredService<ILogger<RoamingRegistry>>(),
            settings.Routing.MaxLocalDeviceRoutes));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RoamingRegistry>());

        builder.Services.AddSingleton<RepeaterRegistry>(sp =>
            new RepeaterRegistry(sp.GetRequiredService<ILogger<RepeaterRegistry>>(), settings.ZoneId, settings.ZonePsk));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RepeaterRegistry>());

        builder.Services.AddSingleton(sp => new MeshDiscoveryService(
            sp.GetRequiredService<ILogger<MeshDiscoveryService>>(),
            sp.GetRequiredService<MasterRegistry>(),
            sp.GetRequiredService<RoamingRegistry>(),
            myZoneId: settings.ZoneId,
            myDataPort: 62031,
            discoveryPort: 42069,
            meshPsk: settings.MeshPsk));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MeshDiscoveryService>());

        builder.Services.AddSingleton(sp => new MicroSubnetRouter(
            sp.GetRequiredService<ILogger<MicroSubnetRouter>>(),
            sp.GetRequiredService<RepeaterRegistry>(),
            sp.GetRequiredService<MasterRegistry>(),
            sp.GetRequiredService<RoamingRegistry>(),
            sp.GetRequiredService<MeshDiscoveryService>(),
            settings.ZoneId,
            settings.Routing.MaxActiveCalls,
            settings.Routing.MaxLocalDeviceRoutes));

        builder.Services.AddHostedService<DmrServer>();
        builder.Services.AddSingleton<SdsGateway>(sp => new SdsGateway(
            sp.GetRequiredService<ILogger<SdsGateway>>(),
            sp.GetRequiredService<MicroSubnetRouter>(),
            settings.Sds.MaxSessions,
            settings.Sds.MaxMessageBytes));

        builder.Services.AddSingleton<RawMqttClient>(_ =>
        {
            var clientId = Encoding.UTF8.GetBytes($"dmroute_{settings.ZoneId}_{Random.Shared.Next(1000, 9999)}");
            return new RawMqttClient(clientId);
        });

        builder.Services.AddHostedService<MqttIntegrationService>();
    }
}
