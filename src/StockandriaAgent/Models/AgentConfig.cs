namespace StockandriaAgent.Models;

public class AgentConfig
{
    public string AgentId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string BackendUrl { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// Connection string de la DB SICAR local (MariaDB/MySQL). Se persiste
    /// cifrada con DPAPI junto con el token del agente.
    /// </summary>
    public string SicarConnectionString { get; set; } = string.Empty;
}
