namespace StockandriaAgent.Models;

/// <summary>
/// Envoltorio estándar de respuestas del backend Stockandria (inyectado por
/// TransformInterceptor). Todas las respuestas JSON del backend tienen esta
/// forma: { success, message, data, timestamp }.
/// </summary>
public class ApiEnvelope<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
    public string? Timestamp { get; set; }
}
