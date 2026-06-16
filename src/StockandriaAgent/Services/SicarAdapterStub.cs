using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace StockandriaAgent.Services;

public class SicarAdapterStub : ISicarAdapter
{
    private readonly ILogger<SicarAdapterStub> _logger;

    public SicarAdapterStub(ILogger<SicarAdapterStub> logger)
    {
        _logger = logger;
    }

    public Task<SicarReachability> TestConnectionAsync(JsonElement? payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] TestConnection → reachable=true");
        return Task.FromResult(new SicarReachability(true, null));
    }

    public Task<List<string>> ListDatabasesAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] ListDatabases");
        return Task.FromResult(new List<string> { "sicar_demo_a", "sicar_demo_b" });
    }

    public Task<object> GetStatusAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { sicarVersion = "3.0", articlesCount = 1500, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> SyncProductsAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { syncedCount = 50, errors = 0, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> SyncStockAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { syncedCount = 50, errors = 0, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> SyncSalesAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { syncedCount = 0, sales = Array.Empty<object>(), stub = true };
        return Task.FromResult(response);
    }

    public Task<object> SyncStockHistoryAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { syncedCount = 0, changes = Array.Empty<object>(), stub = true };
        return Task.FromResult(response);
    }

    public Task<object> SyncSuppliersAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { syncedCount = 10, errors = 0, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> CreateBackupAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new
        {
            fileName = $"backup_{DateTime.UtcNow:yyyy-MM-dd}.sql",
            sizeBytes = 1_024_000,
            stub = true,
        };
        return Task.FromResult(response);
    }

    public Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> UpdatePriceAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> BulkUpdateMinMaxAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { results = Array.Empty<object>(), stub = true });

    public Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> UpdateProductAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> InsertProductAsync(JsonElement payload, CancellationToken ct) =>
        // Stub: simulamos un art_id ficticio para que el flujo end-to-end
        // de dev funcione sin SICAR real corriendo.
        Task.FromResult<object>(new { artId = 999999, clave = payload.TryGetProperty("clave", out var c) ? c.GetString() : "STUB", stub = true });

    public Task<object> BulkInsertProductsAsync(JsonElement payload, CancellationToken ct)
    {
        var count = payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength()
            : 0;
        return Task.FromResult<object>(new { inserted = count, skipped = 0, failed = Array.Empty<object>(), stub = true });
    }

    public Task<object> BulkUpdatePriceAsync(JsonElement payload, CancellationToken ct)
    {
        var count = payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength()
            : 0;
        return Task.FromResult<object>(new { updated = count, failed = Array.Empty<object>(), stub = true });
    }

    public Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { items = Array.Empty<object>(), total = 0, page = 1, limit = 50, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> GetStockAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { items = Array.Empty<object>(), total = 0, page = 1, limit = 50, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> GetTransfersAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { items = Array.Empty<object>(), total = 0, page = 1, limit = 20, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> GetSuppliersAsync(JsonElement payload, CancellationToken ct)
    {
        object response = new { items = Array.Empty<object>(), total = 0, page = 1, limit = 50, stub = true };
        return Task.FromResult(response);
    }

    public Task<object> GetProductMarginsAsync(JsonElement payload, CancellationToken ct)
    {
        object response = Array.Empty<object>();
        return Task.FromResult(response);
    }

    public Task<object> GetCategoriesAsync(JsonElement payload, CancellationToken ct)
    {
        object response = Array.Empty<object>();
        return Task.FromResult(response);
    }

    public Task<object> GetSupplierCategoriesAsync(JsonElement payload, CancellationToken ct)
        => Task.FromResult<object>(new { rows = Array.Empty<object>() });

    public Task<object> CreateDepartmentAsync(JsonElement payload, CancellationToken ct)
        => Task.FromResult<object>(new { depId = 888888, stub = true });

    public Task<object> UpdateDepartmentAsync(JsonElement payload, CancellationToken ct)
        => Task.FromResult<object>(new { depId = payload.TryGetProperty("depId", out var d) ? d.GetInt32() : 0, rowsAffected = 1, stub = true });

    public Task<object> CreateCategoryAsync(JsonElement payload, CancellationToken ct)
        => Task.FromResult<object>(new { catId = 777777, stub = true });

    public Task<object> UpdateCategoryAsync(JsonElement payload, CancellationToken ct)
        => Task.FromResult<object>(new { catId = payload.TryGetProperty("catId", out var c) ? c.GetInt32() : 0, rowsAffected = 1, stub = true });
}
