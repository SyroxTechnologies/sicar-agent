using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public class RegistrationService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema",
        "mysql",
        "performance_schema",
        "sys",
    };

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
                "Configuración cargada: agentId={AgentId}, orgId={OrgId}",
                existing.AgentId,
                existing.OrganizationId);

            // Si la config previa no tiene SicarBaseConnectionString (o tiene una
            // del formato viejo con Database= adentro), corremos el wizard para
            // regenerarla en formato nuevo.
            if (string.IsNullOrWhiteSpace(existing.SicarBaseConnectionString))
            {
                _logger.LogWarning(
                    "La configuración cargada no tiene SicarBaseConnectionString. Ejecutando wizard.");
                existing.SicarBaseConnectionString = ResolveSicarBaseConnectionString();
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
                        "No se encontró link token. Definí STOCKANDRIA_LINK_TOKEN o configurala en Backend:LinkToken. Reintentando en {Delay}s.",
                        RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }

                // Wizard base: host/puerto/user/password. SIN Database=.
                var baseConnectionString = ResolveSicarBaseConnectionString();

                // Listamos las DBs disponibles para reportar al backend.
                // Si falla, seguimos igual — el admin puede escribir el nombre a mano.
                var detectedDatabases = await ListDatabasesSafelyAsync(baseConnectionString, stoppingToken);
                if (detectedDatabases.Count > 0)
                {
                    _logger.LogInformation(
                        "Bases de datos SICAR detectadas: {Dbs}",
                        string.Join(", ", detectedDatabases));
                }

                var backendUrl = _config["Backend:Url"]
                    ?? throw new InvalidOperationException("Falta configuración Backend:Url");
                var name = Environment.MachineName;
                var version = GetAgentVersion();
                var hostInfo = BuildHostInfo(version);

                _logger.LogInformation("Registrando agente contra {BackendUrl} como {Name}", backendUrl, name);

                var response = await _client.RegisterAsync(
                    linkToken, name, version, hostInfo, detectedDatabases, stoppingToken);

                var cfg = new AgentConfig
                {
                    AgentId = response.AgentId,
                    Token = response.Token,
                    BackendUrl = backendUrl,
                    OrganizationId = response.OrganizationId,
                    RegisteredAt = DateTime.UtcNow,
                    SicarBaseConnectionString = baseConnectionString,
                };

                await _storage.SaveAsync(cfg, stoppingToken);
                _session.SetConfig(cfg);

                _logger.LogInformation(
                    "Registro exitoso: agentId={AgentId}, orgId={OrgId}",
                    cfg.AgentId,
                    cfg.OrganizationId);
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
    /// Resuelve la connection string BASE al servidor MySQL (sin Database=).
    /// Orden de prioridad:
    /// 1. Variable STOCKANDRIA_SICAR_BASE_CONNECTION_STRING.
    /// 2. Clave Sicar:BaseConnectionString en appsettings.
    /// 3. Wizard interactivo (host/puerto/user/password).
    /// </summary>
    private string ResolveSicarBaseConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("STOCKANDRIA_SICAR_BASE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            _logger.LogInformation("SicarBaseConnectionString leída desde STOCKANDRIA_SICAR_BASE_CONNECTION_STRING.");
            return fromEnv.Trim();
        }

        var fromConfig = _config["Sicar:BaseConnectionString"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            _logger.LogInformation("SicarBaseConnectionString leída desde config Sicar:BaseConnectionString.");
            return fromConfig.Trim();
        }

        return RunWizard();
    }

    private string RunWizard()
    {
        _logger.LogInformation("Lanzando wizard interactivo del servidor MySQL local.");

        Console.WriteLine();
        Console.WriteLine("=========================================================");
        Console.WriteLine(" Conexión al servidor MySQL local (SICAR)");
        Console.WriteLine("=========================================================");
        Console.WriteLine(" Estos datos son del SERVIDOR MySQL — no de una DB específica.");
        Console.WriteLine(" El agente va a poder conectarse a cualquier base de datos SICAR");
        Console.WriteLine(" que esté en ese servidor. En Stockandria mapeás cada sucursal");
        Console.WriteLine(" con el nombre de su DB correspondiente.");
        Console.WriteLine();

        var host = Prompt("Host", "localhost");
        var port = Prompt("Puerto", "3306");
        var user = Prompt("Usuario", "root");
        var password = PromptSecret("Password");

        var cs = $"Server={host};Port={port};Uid={user};Pwd={password};" +
                 "AllowUserVariables=true;Pooling=true;";
        Console.WriteLine();
        Console.WriteLine("Conexión base armada. Guardando cifrada...");
        Console.WriteLine();
        return cs;
    }

    private async Task<List<string>> ListDatabasesSafelyAsync(
        string baseConnectionString,
        CancellationToken ct)
    {
        try
        {
            await using var conn = new MySqlConnection(baseConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new MySqlCommand("SHOW DATABASES", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var databases = new List<string>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                if (!SystemDatabases.Contains(name))
                {
                    databases.Add(name);
                }
            }
            return databases;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudieron listar las DBs al registrar (continuamos sin la lista)");
            return new List<string>();
        }
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
