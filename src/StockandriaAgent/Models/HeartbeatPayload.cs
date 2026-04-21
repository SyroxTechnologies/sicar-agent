namespace StockandriaAgent.Models;

public class HeartbeatPayload
{
    public bool SicarReachable { get; set; }
    public string? SicarError { get; set; }
    public string? AgentVersion { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}
