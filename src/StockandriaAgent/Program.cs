using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Serilog;
using StockandriaAgent.Commands;
using StockandriaAgent.Services;
using StockandriaAgent.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddEnvironmentVariables(prefix: "STOCKANDRIA_")
    .AddCommandLine(args);

builder.Services.AddSerilog((services, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "StockandriaAgent";
    });
}

builder.Services.AddSingleton<AgentSession>();
builder.Services.AddSingleton<IConfigStorage, ConfigStorage>();

// Seleccionar SicarAdapter real vs stub segun config (default: real si hay
// connection string cargada; el stub sirve para tests locales).
var useStubAdapter = builder.Configuration.GetValue<bool>("Sicar:UseStub");
if (useStubAdapter)
{
    builder.Services.AddSingleton<ISicarAdapter, SicarAdapterStub>();
}
else
{
    builder.Services.AddSingleton<ISicarAdapter, SicarAdapter>();
}

builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<IBackendClient, BackendClient>();

builder.Services
    .AddHttpClient(BackendClient.HttpClientName, (sp, client) =>
    {
        var backendUrl = builder.Configuration["Backend:Url"]
            ?? throw new InvalidOperationException("Falta configuración Backend:Url");
        client.BaseAddress = new Uri(backendUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "StockandriaAgent/0.1.0");
    })
    .AddResilienceHandler("backend-pipeline", pipeline =>
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        });
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30),
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(15));
    });

builder.Services.AddHostedService<RegistrationService>();

// HubWorker mantiene la conexion Socket.io persistente al hub del backend y
// recibe los comandos por push. El propio cliente Socket.io hace heartbeat +
// reconexion, asi que no hace falta un HeartbeatWorker separado.
builder.Services.AddHostedService<HubWorker>();

try
{
    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "StockandriaAgent terminó por una excepción no controlada");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
