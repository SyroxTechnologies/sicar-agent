using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public interface IConfigStorage
{
    Task<AgentConfig?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AgentConfig config, CancellationToken ct = default);
    string StoragePath { get; }
}
