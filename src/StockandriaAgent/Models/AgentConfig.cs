namespace StockandriaAgent.Models;

public class AgentConfig
{
    public string AgentId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string BackendUrl { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// Connection string BASE del servidor MySQL local (sin Database=).
    /// Contiene host, puerto, usuario y password. A esta string se le agrega
    /// dinámicamente `;Database={databaseName}` según qué sucursal pida cada
    /// comando. Se persiste cifrada con DPAPI (Windows) o plaintext 0600 (Linux dev).
    /// </summary>
    public string SicarBaseConnectionString { get; set; } = string.Empty;
}
