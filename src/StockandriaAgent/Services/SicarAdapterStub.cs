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

    public Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

    public Task<object> UpdateProductAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object>(new { ok = true, stub = true });

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
}
