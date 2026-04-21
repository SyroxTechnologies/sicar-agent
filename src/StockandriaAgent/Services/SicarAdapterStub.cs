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

    public Task<SicarReachability> TestConnectionAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] TestConnection → reachable=true");
        return Task.FromResult(new SicarReachability(true, null));
    }

    public Task<object> GetStatusAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] GetStatus");
        object payload = new
        {
            sicarVersion = "3.0",
            tablesCount = 120,
            articlesCount = 1500,
            stub = true,
        };
        return Task.FromResult(payload);
    }

    public Task<object> SyncProductsAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] SyncProducts");
        object payload = new
        {
            syncedCount = 50,
            errors = 0,
            stub = true,
        };
        return Task.FromResult(payload);
    }

    public Task<object> SyncStockAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] SyncStock");
        object payload = new
        {
            syncedCount = 50,
            errors = 0,
            stub = true,
        };
        return Task.FromResult(payload);
    }

    public Task<object> SyncSuppliersAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] SyncSuppliers");
        object payload = new
        {
            syncedCount = 10,
            errors = 0,
            stub = true,
        };
        return Task.FromResult(payload);
    }

    public Task<object> CreateBackupAsync(CancellationToken ct)
    {
        _logger.LogDebug("[STUB] CreateBackup");
        object payload = new
        {
            fileName = $"backup_{DateTime.UtcNow:yyyy-MM-dd}.sql",
            sizeBytes = 1_024_000,
            stub = true,
        };
        return Task.FromResult(payload);
    }

    public Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] AdjustStock {Payload}", payload);
        return Task.FromResult<object>(new { ok = true, stub = true });
    }

    public Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] BulkAdjustStock");
        return Task.FromResult<object>(new { ok = true, stub = true });
    }

    public Task<object> UpdatePriceAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] UpdatePrice {Payload}", payload);
        return Task.FromResult<object>(new { ok = true, stub = true });
    }

    public Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] UpdateMinMax {Payload}", payload);
        return Task.FromResult<object>(new { ok = true, stub = true });
    }

    public Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] TransferStock");
        return Task.FromResult<object>(new { ok = true, stub = true });
    }

    public Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] UpdateSupplier");
        return Task.FromResult<object>(new { ok = true, stub = true });
    }

    public Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] GetProducts mode={Mode}",
            payload.TryGetProperty("mode", out var m) ? m.GetString() : "list");
        object response = new
        {
            items = Array.Empty<object>(),
            total = 0,
            page = 1,
            limit = 50,
            stub = true,
        };
        return Task.FromResult(response);
    }

    public Task<object> GetStockAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] GetStock");
        object response = new
        {
            items = Array.Empty<object>(),
            total = 0,
            page = 1,
            limit = 50,
            stub = true,
        };
        return Task.FromResult(response);
    }

    public Task<object> GetTransfersAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] GetTransfers");
        object response = new
        {
            items = Array.Empty<object>(),
            total = 0,
            page = 1,
            limit = 20,
            stub = true,
        };
        return Task.FromResult(response);
    }

    public Task<object> GetSuppliersAsync(JsonElement payload, CancellationToken ct)
    {
        _logger.LogDebug("[STUB] GetSuppliers");
        object response = new
        {
            items = Array.Empty<object>(),
            total = 0,
            page = 1,
            limit = 50,
            stub = true,
        };
        return Task.FromResult(response);
    }
}
