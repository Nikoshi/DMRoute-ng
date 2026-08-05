using DMRoute_ng.Registry;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

// DI-Registrierung
builder.Services.AddSingleton<RepeaterRegistry>();

using var host = builder.Build();
await host.RunAsync();
