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

    private async Task HandleExecuteCommand(SocketIOResponse response)
    {
        BackendCommand? command = null;
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

            var result = await _dispatcher.DispatchAsync(command, CancellationToken.None);

            if (_socket is not null)
            {
                await _socket.EmitAsync(EvtReportResult, new
                {
                    commandId = command.Id,
                    status = result.Status == "SUCCESS" ? "SUCCESS" : "FAILED",
                    resultPayload = result.ResultPayload,
                    errorMessage = result.ErrorMessage,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando comando {CommandId}", command?.Id);

            if (_socket is not null && command is not null)
            {
                try
                {
                    await _socket.EmitAsync(EvtReportResult, new
                    {
                        commandId = command.Id,
                        status = "FAILED",
                        errorMessage = ex.Message,
                    });
                }
                catch
                {
                    // Si falla reportar, el backend marcara TIMEOUT al vencer.
                }
            }
        }
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
