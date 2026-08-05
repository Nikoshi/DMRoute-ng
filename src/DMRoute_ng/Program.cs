using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DMRoute_ng.Registry;
using DMRoute_ng.Core;

var builder = Host.CreateApplicationBuilder(args);

// Singleton-Zustand
builder.Services.AddSingleton<RepeaterRegistry>();

// Hosted Service (Hält die Konsolenanwendung am Leben und lauscht auf UDP)
builder.Services.AddHostedService<DmrServer>();

using var host = builder.Build();
await host.RunAsync();