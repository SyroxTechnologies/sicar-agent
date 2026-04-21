using System.Text.Json;
using System.Text.Json.Serialization;

namespace StockandriaAgent.Models;

public class BackendCommand
{
    // El hub del backend emite el payload con la clave "commandId"
    // (ver hub-protocol.ts: ExecuteCommandMessage). El polling legacy
    // usaba "id", pero ahora todos los mensajes vienen por Socket.io
    // con el shape del hub — se acepta "commandId" como primario.
    [JsonPropertyName("commandId")]
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? TimeoutAt { get; set; }
}

public class GetCommandResponse
{
    public BackendCommand? Command { get; set; }
}
