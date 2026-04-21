using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public interface IBackendClient
{
    Task<RegisterResponse> RegisterAsync(
        string linkToken,
        string name,
        string? version,
        object? hostInfo,
        IReadOnlyList<string>? detectedDatabases,
        CancellationToken ct);

    Task SendHeartbeatAsync(HeartbeatPayload payload, CancellationToken ct);

    Task<BackendCommand?> GetNextCommandAsync(CancellationToken ct);

    Task SubmitCommandResultAsync(string commandId, CommandResult result, CancellationToken ct);
}
