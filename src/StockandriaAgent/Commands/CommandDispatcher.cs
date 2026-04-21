using System.Text.Json;
using Microsoft.Extensions.Logging;
using StockandriaAgent.Models;
using StockandriaAgent.Services;

namespace StockandriaAgent.Commands;

public class CommandDispatcher
{
    private readonly ISicarAdapter _sicar;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(ISicarAdapter sicar, ILogger<CommandDispatcher> logger)
    {
        _sicar = sicar;
        _logger = logger;
    }

    public async Task<CommandResult> DispatchAsync(BackendCommand command, CancellationToken ct)
    {
        _logger.LogInformation("Ejecutando comando {CommandId} tipo {Type}", command.Id, command.Type);
        try
        {
            var payload = OptionalPayload(command);

            return command.Type switch
            {
                "TEST_CONNECTION" => await HandleTestConnection(payload, ct),
                "GET_STATUS" => CommandResult.Ok(await _sicar.GetStatusAsync(payload, ct)),
                "SYNC_PRODUCTS" => CommandResult.Ok(await _sicar.SyncProductsAsync(payload, ct)),
                "SYNC_STOCK" => CommandResult.Ok(await _sicar.SyncStockAsync(payload, ct)),
                "SYNC_SUPPLIERS" => CommandResult.Ok(await _sicar.SyncSuppliersAsync(payload, ct)),
                "CREATE_BACKUP" => CommandResult.Ok(await _sicar.CreateBackupAsync(payload, ct)),

                "ADJUST_STOCK" => CommandResult.Ok(await _sicar.AdjustStockAsync(payload, ct)),
                "BULK_ADJUST_STOCK" => CommandResult.Ok(await _sicar.BulkAdjustStockAsync(payload, ct)),
                "UPDATE_PRICE" => CommandResult.Ok(await _sicar.UpdatePriceAsync(payload, ct)),
                "UPDATE_MIN_MAX" => CommandResult.Ok(await _sicar.UpdateMinMaxAsync(payload, ct)),
                "TRANSFER_STOCK" => CommandResult.Ok(await _sicar.TransferStockAsync(payload, ct)),
                "UPDATE_SUPPLIER" => CommandResult.Ok(await _sicar.UpdateSupplierAsync(payload, ct)),

                "GET_PRODUCTS" => CommandResult.Ok(await _sicar.GetProductsAsync(payload, ct)),
                "GET_STOCK" => CommandResult.Ok(await _sicar.GetStockAsync(payload, ct)),
                "GET_TRANSFERS" => CommandResult.Ok(await _sicar.GetTransfersAsync(payload, ct)),
                "GET_SUPPLIERS" => CommandResult.Ok(await _sicar.GetSuppliersAsync(payload, ct)),

                _ => UnknownCommand(command.Type),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando comando {CommandId}", command.Id);
            return CommandResult.Fail(ex.Message);
        }
    }

    private async Task<CommandResult> HandleTestConnection(JsonElement payload, CancellationToken ct)
    {
        var result = await _sicar.TestConnectionAsync(payload, ct);
        return result.Reachable
            ? CommandResult.Ok(new { reachable = true })
            : CommandResult.Fail(result.Error ?? "SICAR inaccesible", new { reachable = false });
    }

    private static JsonElement OptionalPayload(BackendCommand command)
    {
        if (command.Payload is null || command.Payload.Value.ValueKind == JsonValueKind.Null)
        {
            // Objeto JSON vacío — los TryGetProperty del adapter devuelven false
            // cuando la propiedad no existe. Si el comando requiere databaseName,
            // RequireDatabaseName lanza una excepción clara.
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
        return command.Payload.Value;
    }

    private CommandResult UnknownCommand(string type)
    {
        var msg = $"Tipo de comando desconocido: {type}";
        _logger.LogWarning(msg);
        return CommandResult.Fail(msg);
    }
}
