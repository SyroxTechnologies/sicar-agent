using StockandriaAgent.Models;

namespace StockandriaAgent.Services;

public class AgentSession
{
    private readonly TaskCompletionSource<AgentConfig> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<AgentConfig> WaitForConfigAsync(CancellationToken ct)
    {
        return _tcs.Task.WaitAsync(ct);
    }

    public void SetConfig(AgentConfig config)
    {
        _tcs.TrySetResult(config);
    }

    public bool IsReady => _tcs.Task.IsCompletedSuccessfully;
}
