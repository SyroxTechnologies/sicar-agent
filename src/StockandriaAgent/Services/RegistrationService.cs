using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public class RegistrationService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly IConfigStorage _storage;
    private readonly AgentSession _session;
    private readonly IBackendClient _client;
    private readonly IConfiguration _config;
    private readonly ILogger<RegistrationService> _logger;

    public RegistrationService(
        IConfigStorage storage,
        AgentSession session,
        IBackendClient client,
        IConfiguration config,
        ILogger<RegistrationService> logger)
    {
        _storage = storage;
        _session = session;
        _client = client;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var existing = await _storage.LoadAsync(stoppingToken);
        if (existing != null)
        {
            _logger.LogInformation(
                "Configuración cargada: agentId={AgentId}, branchId={BranchId}",
                existing.AgentId,
                existing.BranchId);

            // Si la config previa no tiene SicarConnectionString (formato viejo),
            // intentamos resolverla ahora para no dejar al agente en un estado
            // semi-configurado.
            if (string.IsNullOrWhiteSpace(existing.SicarConnectionString))
            {
                _logger.LogWarning(
                    "La configuración cargada no tiene SicarConnectionString. Ejecutando wizard.");
                existing.SicarConnectionString = ResolveSicarConnectionString();
                await _storage.SaveAsync(existing, stoppingToken);
            }

            _session.SetConfig(existing);
            return;
        }

        _logger.LogInformation(
            "Sin configuración previa en {Path}. Iniciando flujo de registro.",
            _storage.StoragePath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var linkToken = ReadLinkToken();
                if (string.IsNullOrWhiteSpace(linkToken))
                {
                    _logger.LogWarning(
                        "No se encontró link token. Definí la variable STOCKANDRIA_LINK_TOKEN o configurala en Backend:LinkToken. Reintentando en {Delay}s.",
                        RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                // Pedimos la connection string ANTES del registro en el backend
                // para no dejar al agente vinculado pero sin acceso a SICAR.
                var sicarConnectionString = ResolveSicarConnectionString();

                var backendUrl = _config["Backend:Url"]
                    ?? throw new InvalidOperationException("Falta configuración Backend:Url");
                var name = Environment.MachineName;
                var version = GetAgentVersion();
                var hostInfo = BuildHostInfo(version);

                _logger.LogInformation("Registrando agente contra {BackendUrl} como {Name}", backendUrl, name);

                var response = await _client.RegisterAsync(linkToken, name, version, hostInfo, stoppingToken);

                var cfg = new AgentConfig
                {
                    AgentId = response.AgentId,
                    Token = response.Token,
                    BackendUrl = backendUrl,
                    BranchId = response.BranchId,
                    OrganizationId = response.OrganizationId,
                    RegisteredAt = DateTime.UtcNow,
                    SicarConnectionString = sicarConnectionString,
                };

                await _storage.SaveAsync(cfg, stoppingToken);
                _session.SetConfig(cfg);

                _logger.LogInformation(
                    "Registro exitoso: agentId={AgentId}, branchId={BranchId}",
                    cfg.AgentId,
                    cfg.BranchId);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error durante el registro. Reintentando en {Delay}s.", RetryDelay.TotalSeconds);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private string? ReadLinkToken()
    {
        var fromConfig = _config["Backend:LinkToken"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig.Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable("STOCKANDRIA_LINK_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return null;
    }

    /// <summary>
    /// Resuelve la connection string a la DB SICAR local. Orden de prioridad:
    /// 1. Variable de entorno STOCKANDRIA_SICAR_CONNECTION_STRING (entera).
    /// 2. Sección Sicar:ConnectionString en appsettings/appsettings.Development.json.
    /// 3. Wizard interactivo por consola (pide host/port/user/password/db).
    /// </summary>
    private string ResolveSicarConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("STOCKANDRIA_SICAR_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            _logger.LogInformation("SicarConnectionString leída desde STOCKANDRIA_SICAR_CONNECTION_STRING.");
            return fromEnv.Trim();
        }

        var fromConfig = _config["Sicar:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            _logger.LogInformation("SicarConnectionString leída desde config Sicar:ConnectionString.");
            return fromConfig.Trim();
        }

        return RunSicarWizard();
    }

    private string RunSicarWizard()
    {
        _logger.LogInformation("Lanzando wizard interactivo de configuración SICAR.");

        Console.WriteLine();
        Console.WriteLine("=========================================================");
        Console.WriteLine(" Configuración de la base de datos SICAR local");
        Console.WriteLine("=========================================================");
        Console.WriteLine(" Ingresá los datos de la DB MariaDB/MySQL de SICAR.");
        Console.WriteLine(" (Podés escapar esto seteando STOCKANDRIA_SICAR_CONNECTION_STRING");
        Console.WriteLine("  o Sicar:ConnectionString en appsettings.)");
        Console.WriteLine();

        var host = Prompt("Host", "localhost");
        var port = Prompt("Puerto", "3306");
        var database = Prompt("Base de datos", "sicar");
        var user = Prompt("Usuario", "root");
        var password = PromptSecret("Password");

        var cs = $"Server={host};Port={port};Database={database};Uid={user};Pwd={password};" +
                 "AllowUserVariables=true;Pooling=true;";
        Console.WriteLine();
        Console.WriteLine("Connection string armada (password oculta). Guardando cifrada...");
        Console.WriteLine();
        return cs;
    }

    private static string Prompt(string label, string? defaultValue = null)
    {
        while (true)
        {
            Console.Write(defaultValue is null ? $"{label}: " : $"{label} [{defaultValue}]: ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(input)) return input;
            if (!string.IsNullOrEmpty(defaultValue)) return defaultValue;
            Console.WriteLine($"  {label} no puede estar vacío.");
        }
    }

    private static string PromptSecret(string label)
    {
        while (true)
        {
            Console.Write($"{label}: ");
            var sb = new System.Text.StringBuilder();

            // Si no hay TTY interactivo, caemos a ReadLine normal.
            if (Console.IsInputRedirected)
            {
                var line = Console.ReadLine() ?? string.Empty;
                if (string.IsNullOrEmpty(line))
                {
                    Console.WriteLine($"  {label} no puede estar vacío.");
                    continue;
                }
                return line;
            }

            ConsoleKeyInfo key;
            while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
            {
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    sb.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
            Console.WriteLine();

            if (sb.Length == 0)
            {
                Console.WriteLine($"  {label} no puede estar vacío.");
                continue;
            }
            return sb.ToString();
        }
    }

    private static string GetAgentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v?.ToString() ?? "0.0.0";
    }

    private static object BuildHostInfo(string version) => new
    {
        machineName = Environment.MachineName,
        osDescription = RuntimeInformation.OSDescription,
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        frameworkDescription = RuntimeInformation.FrameworkDescription,
        processorCount = Environment.ProcessorCount,
        userName = Environment.UserName,
        agentVersion = version,
    };
}
