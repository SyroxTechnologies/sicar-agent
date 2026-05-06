using System.Text.Json;

namespace StockandriaAgent.Services;

public record SicarReachability(bool Reachable, string? Error);

/// <summary>
/// Abstracción del acceso a SICAR. En el modelo multi-sucursal, una única
/// instalación de SICAR en la PC del cliente tiene múltiples bases de datos
/// (una por sucursal: sicar_norte, sicar_chihuahua, etc.). Todos los métodos
/// (excepto <see cref="ListDatabasesAsync"/>) leen el nombre de la DB destino
/// desde el campo `databaseName` del payload.
/// </summary>
public interface ISicarAdapter
{
    /// <summary>
    /// Prueba conexión al servidor MySQL base. Si el payload incluye
    /// `databaseName`, también valida que esa DB exista.
    /// </summary>
    Task<SicarReachability> TestConnectionAsync(JsonElement? payload, CancellationToken ct);

    /// <summary>
    /// Lista las bases de datos visibles en el servidor MySQL. Se usa al
    /// registrar el agente para mostrar un dropdown en el admin.
    /// </summary>
    Task<List<string>> ListDatabasesAsync(CancellationToken ct);

    Task<object> GetStatusAsync(JsonElement payload, CancellationToken ct);
    Task<object> SyncProductsAsync(JsonElement payload, CancellationToken ct);
    Task<object> SyncStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> SyncSuppliersAsync(JsonElement payload, CancellationToken ct);
    Task<object> CreateBackupAsync(JsonElement payload, CancellationToken ct);

    Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdatePriceAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct);
    Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateProductAsync(JsonElement payload, CancellationToken ct);

    Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetTransfersAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetSuppliersAsync(JsonElement payload, CancellationToken ct);
}
