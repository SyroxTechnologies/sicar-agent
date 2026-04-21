using System.Text.Json;

namespace StockandriaAgent.Services;

public record SicarReachability(bool Reachable, string? Error);

public interface ISicarAdapter
{
    Task<SicarReachability> TestConnectionAsync(CancellationToken ct);
    Task<object> GetStatusAsync(CancellationToken ct);
    Task<object> SyncProductsAsync(CancellationToken ct);
    Task<object> SyncStockAsync(CancellationToken ct);
    Task<object> SyncSuppliersAsync(CancellationToken ct);
    Task<object> CreateBackupAsync(CancellationToken ct);

    // Operaciones de escritura (solo UPDATE sobre la DB SICAR — nunca INSERT ni DELETE).
    Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdatePriceAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct);
    Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct);

    // Operaciones de lectura por el hub. El backend ya no se conecta a la DB SICAR:
    // cada GET del controller encola uno de estos comandos y espera el resultado.
    // payload.mode decide la variante ("list" | "detail" | "categories").
    Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetTransfersAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetSuppliersAsync(JsonElement payload, CancellationToken ct);
}
