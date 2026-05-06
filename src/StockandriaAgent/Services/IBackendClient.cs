using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public interface IBackendClient
{
    Task<RegisterResponse> RegisterAsync(
        string linkToken,
        string name,
        string installationId,
        string databaseName,
        string? version,
        object? hostInfo,
        IReadOnlyList<string>? detectedDatabases,
        CancellationToken ct);

    Task LinkBranchAsync(string linkToken, string databaseName, CancellationToken ct);

    Task SendHeartbeatAsync(HeartbeatPayload payload, CancellationToken ct);

    Task<BackendCommand?> GetNextCommandAsync(CancellationToken ct);

    Task SubmitCommandResultAsync(string commandId, CommandResult result, CancellationToken ct);
}
