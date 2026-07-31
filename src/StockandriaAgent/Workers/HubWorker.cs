using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using StockandriaAgent.Commands;
using StockandriaAgent.Models;
using StockandriaAgent.Services;

namespace StockandriaAgent.Workers;

/// <summary>
/// Reemplaza al antiguo <c>CommandPollingWorker</c>. Abre una conexión Socket.io
/// persistente al hub del backend y despacha los comandos a medida que llegan
/// por push. Reconexión automática la maneja la librería.
/// </summary>
public class HubWorker : BackgroundService
{
    private const string Namespace = "/agent-hub";
    private const string EvtExecuteCommand = "execute-command";
    private const string EvtReportResult = "report-result";
    private const string EvtAgentReady = "agent-ready";
    private const string EvtAgentHeartbeat = "agent-heartbeat";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    // Reporte del resultado: cuantas veces reintentar y cuanto esperar a que la
    // libreria reconecte antes de cada intento. El backend le da 24hs de plazo a
    // un comando, asi que insistir un par de minutos es barato al lado de perder
    // el resultado de un mes entero de ventas.
    private const int MaxReportAttempts = 6;
    private static readonly TimeSpan ReportRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectWaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AgentSession _session;
    private readonly CommandDispatcher _dispatcher;
    private readonly ISicarAdapter _sicar;
    private readonly IConfiguration _config;
    private readonly ILogger<HubWorker> _logger;

    private SocketIOClient.SocketIO? _socket;

    public HubWorker(
        AgentSession session,
        CommandDispatcher dispatcher,
        ISicarAdapter sicar,
        IConfiguration config,
        ILogger<HubWorker> logger)
    {
        _session = session;
        _dispatcher = dispatcher;
        _sicar = sicar;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var agentConfig = await _session.WaitForConfigAsync(stoppingToken);

        var baseUrl = _config["Backend:Url"]
            ?? throw new InvalidOperationException("Falta configuración Backend:Url");

        var hubUrl = baseUrl.TrimEnd('/') + Namespace;
        _socket = new SocketIOClient.SocketIO(hubUrl, new SocketIOOptions
        {
            Auth = new Dictionary<string, string> { ["token"] = agentConfig.Token },
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            ReconnectionDelay = 1_000,
            ReconnectionDelayMax = 30_000,
        });

        _socket.OnConnected += OnConnected;
        _socket.OnDisconnected += OnDisconnected;
        _socket.OnError += OnError;
        _socket.OnReconnectAttempt += (_, attempt) =>
            _logger.LogWarning("Reintentando conexión al hub (intento {Attempt})", attempt);

        _socket.On(EvtExecuteCommand, async response => await HandleExecuteCommand(response));

        try
        {
            await _socket.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo conectar al hub en {Url}", hubUrl);
        }

        // Loop de heartbeat periodico mientras el worker este vivo.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
                await SendHeartbeatAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }

        if (_socket.Connected)
        {
            await _socket.DisconnectAsync();
        }
    }

    private async void OnConnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Conectado al hub del backend");
        if (_socket is null) return;

        try
        {
            await _socket.EmitAsync(EvtAgentReady, new
            {
                agentVersion = typeof(HubWorker).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                hostInfo = new
                {
                    machineName = Environment.MachineName,
                    osVersion = Environment.OSVersion.VersionString,
                },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo emitir {Event}", EvtAgentReady);
        }
    }

    private void OnDisconnected(object? sender, string reason)
    {
        _logger.LogWarning("Desconectado del hub: {Reason}", reason);
    }

    private void OnError(object? sender, string error)
    {
        _logger.LogError("Error de Socket.io: {Error}", error);
    }

    /// <summary>
    /// Ejecuta el comando y reporta el resultado. EJECUTAR y REPORTAR van por
    /// separado a proposito: antes, si el socket se caia justo al emitir, la
    /// excepcion del emit se trataba como si el comando hubiera fallado y el
    /// trabajo ya hecho (por ejemplo, un mes entero de ventas leido de SICAR) se
    /// perdia sin dejar rastro. Ahora el resultado se guarda y se reintenta.
    /// </summary>
    private async Task HandleExecuteCommand(SocketIOResponse response)
    {
        BackendCommand? command = null;
        CommandResult result;

        try
        {
            var payload = response.GetValue<JsonElement>();
            command = JsonSerializer.Deserialize<BackendCommand>(
                payload.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (command is null)
            {
                _logger.LogError("Mensaje execute-command llegó vacío");
                return;
            }

            // ACK al backend — la librería NestJS socket.io usa esto para
            // confirmar recepción antes del resultado final.
            await response.CallbackAsync(new { received = true });

            _logger.LogInformation("Comando recibido: id={CommandId} type={Type}",
                command.Id, command.Type);

            result = await _dispatcher.DispatchAsync(command, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando comando {CommandId}", command?.Id);
            if (command is null) return;
            result = CommandResult.Fail(ex.Message);
        }

        await ReportResultAsync(command.Id, result);
    }

    /// <summary>
    /// Emite el resultado al hub, esperando la reconexion y reintentando si el
    /// socket se cayo. Un emit sobre un socket que se acaba de desconectar tira
    /// ObjectDisposedException dentro de la libreria, asi que no alcanza con
    /// mirar `Connected` una sola vez: hay que reintentar de verdad.
    /// </summary>
    private async Task ReportResultAsync(string commandId, CommandResult result)
    {
        var payload = new
        {
            commandId,
            status = result.Status == CommandResultStatus.Success
                ? CommandResultStatus.Success
                : CommandResultStatus.Failed,
            resultPayload = result.ResultPayload,
            errorMessage = result.ErrorMessage,
        };

        for (var intento = 1; intento <= MaxReportAttempts; intento++)
        {
            try
            {
                if (await WaitForConnectionAsync())
                {
                    await _socket!.EmitAsync(EvtReportResult, payload);
                    if (intento > 1)
                    {
                        _logger.LogInformation(
                            "Resultado de {CommandId} reportado en el intento {Intento}",
                            commandId, intento);
                    }
                    return;
                }

                _logger.LogWarning(
                    "Sin conexión al hub para reportar {CommandId} (intento {Intento}/{Total})",
                    commandId, intento, MaxReportAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Falló el envío del resultado de {CommandId} (intento {Intento}/{Total})",
                    commandId, intento, MaxReportAttempts);
            }

            if (intento < MaxReportAttempts)
            {
                await Task.Delay(ReportRetryDelay);
            }
        }

        // Se agotaron los reintentos: el backend lo va a dar por vencido. Queda
        // logueado con el id para poder resincronizar ese rango a mano.
        _logger.LogError(
            "No se pudo reportar el resultado del comando {CommandId} después de {Total} intentos. " +
            "El backend lo va a marcar como vencido; hay que volver a sincronizar ese rango.",
            commandId, MaxReportAttempts);
    }

    /// <summary>
    /// Espera a que la libreria termine de reconectar, hasta un tope. Devuelve
    /// false si sigue caido: ahi el llamador reintenta o se rinde.
    /// </summary>
    private async Task<bool> WaitForConnectionAsync()
    {
        if (_socket is null) return false;
        if (_socket.Connected) return true;

        var limite = DateTime.UtcNow + ReconnectWaitTimeout;
        while (DateTime.UtcNow < limite)
        {
            await Task.Delay(ReconnectPollInterval);
            if (_socket.Connected) return true;
        }
        return false;
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        if (_socket is null || !_socket.Connected)
        {
            return;
        }

        var version = typeof(HubWorker).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        // Heartbeat: probamos conectividad al servidor MySQL base (sin database).
        // Esto verifica que el agente pueda alcanzar el MySQL; el chequeo por DB
        // específica se hace en cada comando individual.
        SicarReachability reachability;
        try
        {
            reachability = await _sicar.TestConnectionAsync(null, ct);
        }
        catch (Exception ex)
        {
            reachability = new SicarReachability(false, ex.Message);
        }

        try
        {
            await _socket.EmitAsync(EvtAgentHeartbeat, new
            {
                sicarReachable = reachability.Reachable,
                sicarError = reachability.Error,
                agentVersion = version,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo emitir {Event}", EvtAgentHeartbeat);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_socket is not null && _socket.Connected)
        {
            await _socket.DisconnectAsync();
        }
        await base.StopAsync(cancellationToken);
    }
}
