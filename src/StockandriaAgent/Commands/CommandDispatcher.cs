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
            return command.Type switch
            {
                "TEST_CONNECTION" => await HandleTestConnection(ct),
                "GET_STATUS" => CommandResult.Ok(await _sicar.GetStatusAsync(ct)),
                "SYNC_PRODUCTS" => CommandResult.Ok(await _sicar.SyncProductsAsync(ct)),
                "SYNC_STOCK" => CommandResult.Ok(await _sicar.SyncStockAsync(ct)),
                "SYNC_SUPPLIERS" => CommandResult.Ok(await _sicar.SyncSuppliersAsync(ct)),
                "CREATE_BACKUP" => CommandResult.Ok(await _sicar.CreateBackupAsync(ct)),

                "ADJUST_STOCK" => CommandResult.Ok(
                    await _sicar.AdjustStockAsync(RequirePayload(command), ct)),
                "BULK_ADJUST_STOCK" => CommandResult.Ok(
                    await _sicar.BulkAdjustStockAsync(RequirePayload(command), ct)),
                "UPDATE_PRICE" => CommandResult.Ok(
                    await _sicar.UpdatePriceAsync(RequirePayload(command), ct)),
                "UPDATE_MIN_MAX" => CommandResult.Ok(
                    await _sicar.UpdateMinMaxAsync(RequirePayload(command), ct)),
                "TRANSFER_STOCK" => CommandResult.Ok(
                    await _sicar.TransferStockAsync(RequirePayload(command), ct)),
                "UPDATE_SUPPLIER" => CommandResult.Ok(
                    await _sicar.UpdateSupplierAsync(RequirePayload(command), ct)),

                "GET_PRODUCTS" => CommandResult.Ok(
                    await _sicar.GetProductsAsync(OptionalPayload(command), ct)),
                "GET_STOCK" => CommandResult.Ok(
                    await _sicar.GetStockAsync(OptionalPayload(command), ct)),
                "GET_TRANSFERS" => CommandResult.Ok(
                    await _sicar.GetTransfersAsync(OptionalPayload(command), ct)),
                "GET_SUPPLIERS" => CommandResult.Ok(
                    await _sicar.GetSuppliersAsync(OptionalPayload(command), ct)),

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

    private async Task<CommandResult> HandleTestConnection(CancellationToken ct)
    {
        var result = await _sicar.TestConnectionAsync(ct);
        return result.Reachable
            ? CommandResult.Ok(new { reachable = true })
            : CommandResult.Fail(result.Error ?? "SICAR inaccesible", new { reachable = false });
    }

    private static JsonElement RequirePayload(BackendCommand command)
    {
        if (command.Payload is null || command.Payload.Value.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException($"Comando {command.Type} requiere payload");
        }
        return command.Payload.Value;
    }

    private static JsonElement OptionalPayload(BackendCommand command)
    {
        if (command.Payload is null || command.Payload.Value.ValueKind == JsonValueKind.Null)
        {
            // Devolvemos un objeto JSON vacio para que los TryGetProperty del adapter
            // devuelvan false en lugar de lanzar excepcion.
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
