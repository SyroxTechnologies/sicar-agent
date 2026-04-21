using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public class BackendClient : IBackendClient
{
    public const string HttpClientName = "Backend";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _factory;
    private readonly AgentSession _session;
    private readonly ILogger<BackendClient> _logger;

    public BackendClient(
        IHttpClientFactory factory,
        AgentSession session,
        ILogger<BackendClient> logger)
    {
        _factory = factory;
        _session = session;
        _logger = logger;
    }

    public async Task<RegisterResponse> RegisterAsync(
        string linkToken,
        string name,
        string? version,
        object? hostInfo,
        CancellationToken ct)
    {
        using var client = _factory.CreateClient(HttpClientName);
        var body = new
        {
            linkToken,
            name,
            version,
            hostInfo,
        };

        using var response = await client.PostAsJsonAsync("/agent/register", body, JsonOptions, ct);
        await EnsureSuccessAsync(response, "register", ct);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<RegisterResponse>>(JsonOptions, ct);
        if (envelope?.Data is null)
        {
            throw new InvalidOperationException("Respuesta vacía al registrar el agente");
        }
        return envelope.Data;
    }

    public async Task SendHeartbeatAsync(HeartbeatPayload payload, CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(ct);
        using var response = await client.PostAsJsonAsync("/agent/heartbeat", payload, JsonOptions, ct);
        await EnsureSuccessAsync(response, "heartbeat", ct);
    }

    public async Task<BackendCommand?> GetNextCommandAsync(CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(ct);
        using var response = await client.GetAsync("/agent/commands/next", ct);
        await EnsureSuccessAsync(response, "commands/next", ct);

        if (response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        var envelope = await response.Content.ReadFromJsonAsync<GetCommandResponse>(JsonOptions, ct);
        return envelope?.Command;
    }

    public async Task SubmitCommandResultAsync(string commandId, CommandResult result, CancellationToken ct)
    {
        using var client = await CreateAuthenticatedClientAsync(ct);
        using var response = await client.PostAsJsonAsync(
            $"/agent/commands/{commandId}/result",
            result,
            JsonOptions,
            ct);
        await EnsureSuccessAsync(response, $"commands/{commandId}/result", ct);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(CancellationToken ct)
    {
        var config = await _session.WaitForConfigAsync(ct);
        var client = _factory.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        return client;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogWarning(
            "Backend respondió {Status} en {Action}: {Body}",
            (int)response.StatusCode,
            action,
            body);
        response.EnsureSuccessStatusCode();
    }
}
