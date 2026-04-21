using System.Text.Json;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace StockandriaAgent.Services;

/// <summary>
/// Implementación real de <see cref="ISicarAdapter"/> contra la DB MariaDB/MySQL
/// local de SICAR. Regla estricta: solo SELECT y UPDATE. Nunca INSERT ni DELETE
/// — la auditoría vive en Stockandria, no en SICAR.
/// </summary>
public class SicarAdapter : ISicarAdapter
{
    private readonly AgentSession _session;
    private readonly ILogger<SicarAdapter> _logger;

    public SicarAdapter(AgentSession session, ILogger<SicarAdapter> logger)
    {
        _session = session;
        _logger = logger;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var config = await _session.WaitForConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(config.SicarConnectionString))
        {
            throw new InvalidOperationException(
                "SicarConnectionString no está configurada. Ejecutar el wizard de configuración del agente.");
        }

        var conn = new MySqlConnection(config.SicarConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<SicarReachability> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = new MySqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(ct);
            return new SicarReachability(true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TestConnection falló");
            return new SicarReachability(false, ex.Message);
        }
    }

    public async Task<object> GetStatusAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var version = await ScalarString(conn, "SELECT VERSION()", ct);
        var articlesCount = await ScalarLong(conn, "SELECT COUNT(*) FROM articulo", ct);
        var suppliersCount = await ScalarLong(conn, "SELECT COUNT(*) FROM proveedor", ct);
        var departmentsCount = await ScalarLong(conn, "SELECT COUNT(*) FROM departamento", ct);

        return new
        {
            sicarVersion = version,
            articlesCount,
            suppliersCount,
            departmentsCount,
        };
    }

    public async Task<object> SyncProductsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var products = new List<Dictionary<string, object?>>();

        const string sql = @"
            SELECT a.art_id, a.clave, a.descripcion, a.precio1, a.precio2, a.precio3, a.precio4,
                   a.precioCompra, a.existencia, a.invMin, a.invMax, a.cat_id, a.status,
                   c.nombre AS categoria_nombre,
                   (SELECT pa.pro_id
                    FROM proveedorarticulo pa
                    WHERE pa.art_id = a.art_id
                    LIMIT 1) AS proveedor_pro_id
            FROM articulo a
            LEFT JOIN categoria c ON c.cat_id = a.cat_id";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            products.Add(ReadRow(reader));
        }

        return new { syncedCount = products.Count, products };
    }

    public async Task<object> SyncStockAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var stock = new List<Dictionary<string, object?>>();

        const string sql = @"
            SELECT art_id, clave, existencia, invMin, invMax
            FROM articulo
            WHERE status = 1";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stock.Add(ReadRow(reader));
        }

        return new { syncedCount = stock.Count, stock };
    }

    public async Task<object> SyncSuppliersAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var suppliers = new List<Dictionary<string, object?>>();

        // SICAR usa `domicilio` y `mail` (no `direccion`/`correo`).
        // Aliaseamos para que el backend reciba siempre los mismos nombres.
        const string sql = @"
            SELECT pro_id,
                   nombre,
                   alias,
                   rfc,
                   domicilio AS direccion,
                   telefono,
                   mail AS correo,
                   diasCredito,
                   status
            FROM proveedor";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            suppliers.Add(ReadRow(reader));
        }

        return new { syncedCount = suppliers.Count, suppliers };
    }

    public Task<object> CreateBackupAsync(CancellationToken ct)
    {
        // TODO: implementar con mysqldump o equivalente + presigned URL.
        throw new NotImplementedException("CreateBackup todavía no está implementado");
    }

    // -------------------------------------------------------------------------
    // Operaciones de escritura — SOLO UPDATE. Nunca INSERT ni DELETE.
    // -------------------------------------------------------------------------

    public async Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct)
    {
        var artId = payload.GetProperty("artId").GetInt32();
        var newStock = payload.GetProperty("newStock").GetInt32();

        await using var conn = await OpenAsync(ct);
        await using var cmd = new MySqlCommand(
            "UPDATE articulo SET existencia = @stock WHERE art_id = @id",
            conn);
        cmd.Parameters.AddWithValue("@stock", newStock);
        cmd.Parameters.AddWithValue("@id", artId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("ADJUST_STOCK art_id={ArtId} newStock={NewStock} rows={Rows}",
            artId, newStock, rows);

        return new { artId, newStock, rowsAffected = rows };
    }

    public async Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct)
    {
        var adjustments = payload.GetProperty("adjustments");
        if (adjustments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("payload.adjustments debe ser un array");
        }

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var results = new List<object>();
        try
        {
            foreach (var adj in adjustments.EnumerateArray())
            {
                var artId = adj.GetProperty("artId").GetInt32();
                var newStock = adj.GetProperty("newStock").GetInt32();

                await using var cmd = new MySqlCommand(
                    "UPDATE articulo SET existencia = @stock WHERE art_id = @id",
                    conn, tx);
                cmd.Parameters.AddWithValue("@stock", newStock);
                cmd.Parameters.AddWithValue("@id", artId);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                results.Add(new { artId, newStock, rowsAffected = rows });
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        return new { results };
    }

    public async Task<object> UpdatePriceAsync(JsonElement payload, CancellationToken ct)
    {
        var artId = payload.GetProperty("artId").GetInt32();
        var prices = payload.GetProperty("prices");

        var sets = new List<string>();
        var cmd = new MySqlCommand();

        foreach (var prop in new[] { "precio1", "precio2", "precio3", "precio4" })
        {
            if (prices.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            {
                sets.Add($"{prop} = @{prop}");
                cmd.Parameters.AddWithValue($"@{prop}", val.GetDecimal());
            }
        }

        if (sets.Count == 0)
        {
            throw new InvalidOperationException("No se envió ningún precio para actualizar");
        }

        await using var conn = await OpenAsync(ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
        cmd.Parameters.AddWithValue("@id", artId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_PRICE art_id={ArtId} rows={Rows}", artId, rows);
        return new { artId, rowsAffected = rows };
    }

    public async Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct)
    {
        var artId = payload.GetProperty("artId").GetInt32();
        var values = payload.GetProperty("values");

        var sets = new List<string>();
        var cmd = new MySqlCommand();

        if (values.TryGetProperty("invMin", out var min) && min.ValueKind == JsonValueKind.Number)
        {
            sets.Add("invMin = @invMin");
            cmd.Parameters.AddWithValue("@invMin", min.GetInt32());
        }
        if (values.TryGetProperty("invMax", out var max) && max.ValueKind == JsonValueKind.Number)
        {
            sets.Add("invMax = @invMax");
            cmd.Parameters.AddWithValue("@invMax", max.GetInt32());
        }

        if (sets.Count == 0)
        {
            throw new InvalidOperationException("No se envió invMin ni invMax");
        }

        await using var conn = await OpenAsync(ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
        cmd.Parameters.AddWithValue("@id", artId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_MIN_MAX art_id={ArtId} rows={Rows}", artId, rows);
        return new { artId, rowsAffected = rows };
    }

    public async Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct)
    {
        // El backend encola DOS comandos (uno por sucursal) — cada agente recibe
        // solo el lado que le toca. El campo direction decide si se resta o se suma.
        var direction = payload.TryGetProperty("direction", out var dirVal)
            && dirVal.ValueKind == JsonValueKind.String
            ? dirVal.GetString()
            : null;

        if (direction != "DECREMENT" && direction != "INCREMENT")
        {
            throw new InvalidOperationException(
                "payload.direction debe ser 'DECREMENT' o 'INCREMENT'");
        }

        var items = payload.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("payload.items debe ser un array");
        }

        var sql = direction == "DECREMENT"
            ? "UPDATE articulo SET existencia = GREATEST(existencia - @qty, 0) WHERE art_id = @id"
            : "UPDATE articulo SET existencia = existencia + @qty WHERE art_id = @id";

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var results = new List<object>();
        try
        {
            foreach (var item in items.EnumerateArray())
            {
                var artId = item.GetProperty("artId").GetInt32();
                var cantidad = item.GetProperty("cantidad").GetInt32();

                await using var cmd = new MySqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("@qty", cantidad);
                cmd.Parameters.AddWithValue("@id", artId);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                results.Add(new { artId, direction, applied = cantidad, rowsAffected = rows });
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "TRANSFER_STOCK ejecutado ({Direction}) con {Count} items", direction, results.Count);
        return new { direction, results };
    }

    public async Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct)
    {
        var proId = payload.GetProperty("proId").GetInt32();
        var fields = payload.GetProperty("fields");

        var allowed = new[] { "nombre", "alias", "rfc", "direccion", "telefono", "correo", "diasCredito" };
        var sets = new List<string>();
        var cmd = new MySqlCommand();

        foreach (var field in allowed)
        {
            if (fields.TryGetProperty(field, out var val) && val.ValueKind != JsonValueKind.Null)
            {
                sets.Add($"{field} = @{field}");
                cmd.Parameters.AddWithValue($"@{field}", ToSqlValue(val));
            }
        }

        if (sets.Count == 0)
        {
            throw new InvalidOperationException("No se envió ningún campo para actualizar");
        }

        await using var conn = await OpenAsync(ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE proveedor SET {string.Join(", ", sets)} WHERE pro_id = @id";
        cmd.Parameters.AddWithValue("@id", proId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_SUPPLIER pro_id={ProId} rows={Rows}", proId, rows);
        return new { proId, rowsAffected = rows };
    }

    // -------------------------------------------------------------------------
    // Operaciones de lectura — SOLO SELECT. Reemplazan el Prisma directo del
    // backend. Cada comando usa payload.mode para elegir la variante.
    // -------------------------------------------------------------------------

    public async Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct)
    {
        var mode = GetMode(payload);

        if (mode == "detail")
        {
            var artId = payload.GetProperty("artId").GetInt32();
            await using var conn = await OpenAsync(ct);

            const string sql = @"
                SELECT a.art_id, a.clave, a.descripcion, a.precioCompra, a.precio1, a.precio2,
                       a.precio3, a.precio4, a.existencia, a.invMin, a.invMax, a.cat_id,
                       a.localizacion, a.status, a.claveAlterna, a.preCompraProm,
                       c.nombre AS categoria_nombre,
                       d.dep_id, d.nombre AS departamento_nombre
                FROM articulo a
                LEFT JOIN categoria c ON c.cat_id = a.cat_id
                LEFT JOIN departamento d ON d.dep_id = c.dep_id
                WHERE a.art_id = @id";

            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", artId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"Artículo {artId} no encontrado en SICAR");
            }

            return ReadRow(reader);
        }

        if (mode == "categories")
        {
            await using var conn = await OpenAsync(ct);
            var categories = new List<Dictionary<string, object?>>();

            const string sql = @"
                SELECT c.cat_id, c.nombre, c.status, c.dep_id,
                       d.nombre AS departamento_nombre
                FROM categoria c
                LEFT JOIN departamento d ON d.dep_id = c.dep_id
                WHERE c.status = 1
                ORDER BY c.nombre ASC";

            await using var cmd = new MySqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                categories.Add(ReadRow(reader));
            }

            return categories;
        }

        // mode == "list"
        var (page, limit, skip) = GetPagination(payload, defaultLimit: 50, maxLimit: 200);
        var search = GetOptionalString(payload, "search");
        var catId = GetOptionalInt(payload, "catId");

        await using var listConn = await OpenAsync(ct);

        var where = "WHERE a.status = 1";
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrEmpty(search))
        {
            where += " AND (a.clave LIKE @search OR a.descripcion LIKE @search)";
            parameters.Add(("@search", $"%{search}%"));
        }
        if (catId.HasValue)
        {
            where += " AND a.cat_id = @catId";
            parameters.Add(("@catId", catId.Value));
        }

        var items = new List<Dictionary<string, object?>>();
        var listSql = $@"
            SELECT a.art_id, a.clave, a.descripcion, a.precioCompra, a.precio1, a.precio2,
                   a.precio3, a.precio4, a.existencia, a.invMin, a.invMax, a.cat_id,
                   a.localizacion, a.status, c.nombre AS categoria_nombre
            FROM articulo a
            LEFT JOIN categoria c ON c.cat_id = a.cat_id
            {where}
            ORDER BY a.descripcion ASC
            LIMIT @limit OFFSET @skip";

        await using (var listCmd = new MySqlCommand(listSql, listConn))
        {
            foreach (var (name, value) in parameters)
            {
                listCmd.Parameters.AddWithValue(name, value);
            }
            listCmd.Parameters.AddWithValue("@limit", limit);
            listCmd.Parameters.AddWithValue("@skip", skip);
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadRow(reader));
            }
        }

        var countSql = $"SELECT COUNT(*) FROM articulo a {where}";
        long total;
        await using (var countCmd = new MySqlCommand(countSql, listConn))
        {
            foreach (var (name, value) in parameters)
            {
                countCmd.Parameters.AddWithValue(name, value);
            }
            var result = await countCmd.ExecuteScalarAsync(ct);
            total = result is null ? 0 : Convert.ToInt64(result);
        }

        return new { items, total, page, limit };
    }

    public async Task<object> GetStockAsync(JsonElement payload, CancellationToken ct)
    {
        var mode = GetMode(payload);

        if (mode == "detail")
        {
            var artId = payload.GetProperty("artId").GetInt32();
            await using var conn = await OpenAsync(ct);

            const string sql = @"
                SELECT art_id, clave, descripcion, existencia, invMin, invMax
                FROM articulo
                WHERE art_id = @id";

            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", artId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"Artículo {artId} no encontrado en SICAR");
            }

            return ReadRow(reader);
        }

        // mode == "list"
        var (page, limit, skip) = GetPagination(payload, defaultLimit: 50, maxLimit: 200);
        var search = GetOptionalString(payload, "search");
        var belowMin = GetOptionalBool(payload, "belowMin") ?? false;

        await using var listConn = await OpenAsync(ct);

        var where = "WHERE a.status = 1";
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrEmpty(search))
        {
            where += " AND (a.clave LIKE @search OR a.descripcion LIKE @search)";
            parameters.Add(("@search", $"%{search}%"));
        }
        if (belowMin)
        {
            where += " AND a.existencia < a.invMin";
        }

        var items = new List<Dictionary<string, object?>>();
        var listSql = $@"
            SELECT a.art_id, a.clave, a.descripcion, a.existencia, a.invMin, a.invMax,
                   c.nombre AS categoria_nombre
            FROM articulo a
            LEFT JOIN categoria c ON c.cat_id = a.cat_id
            {where}
            ORDER BY a.descripcion ASC
            LIMIT @limit OFFSET @skip";

        await using (var listCmd = new MySqlCommand(listSql, listConn))
        {
            foreach (var (name, value) in parameters)
            {
                listCmd.Parameters.AddWithValue(name, value);
            }
            listCmd.Parameters.AddWithValue("@limit", limit);
            listCmd.Parameters.AddWithValue("@skip", skip);
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadRow(reader));
            }
        }

        var countSql = $"SELECT COUNT(*) FROM articulo a {where}";
        long total;
        await using (var countCmd = new MySqlCommand(countSql, listConn))
        {
            foreach (var (name, value) in parameters)
            {
                countCmd.Parameters.AddWithValue(name, value);
            }
            var result = await countCmd.ExecuteScalarAsync(ct);
            total = result is null ? 0 : Convert.ToInt64(result);
        }

        return new { items, total, page, limit };
    }

    public async Task<object> GetTransfersAsync(JsonElement payload, CancellationToken ct)
    {
        var (page, limit, skip) = GetPagination(payload, defaultLimit: 20, maxLimit: 100);
        var status = GetOptionalInt(payload, "status");

        await using var conn = await OpenAsync(ct);

        var where = "";
        var parameters = new List<(string Name, object Value)>();
        if (status.HasValue)
        {
            where = "WHERE status = @status";
            parameters.Add(("@status", status.Value));
        }

        var items = new List<Dictionary<string, object?>>();
        var listSql = $@"
            SELECT tra_id, folio, fecha, str_id, status
            FROM traspaso
            {where}
            ORDER BY tra_id DESC
            LIMIT @limit OFFSET @skip";

        await using (var listCmd = new MySqlCommand(listSql, conn))
        {
            foreach (var (name, value) in parameters)
            {
                listCmd.Parameters.AddWithValue(name, value);
            }
            listCmd.Parameters.AddWithValue("@limit", limit);
            listCmd.Parameters.AddWithValue("@skip", skip);
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadRow(reader));
            }
        }

        var countSql = $"SELECT COUNT(*) FROM traspaso {where}";
        long total;
        await using (var countCmd = new MySqlCommand(countSql, conn))
        {
            foreach (var (name, value) in parameters)
            {
                countCmd.Parameters.AddWithValue(name, value);
            }
            var result = await countCmd.ExecuteScalarAsync(ct);
            total = result is null ? 0 : Convert.ToInt64(result);
        }

        return new { items, total, page, limit };
    }

    public async Task<object> GetSuppliersAsync(JsonElement payload, CancellationToken ct)
    {
        var mode = GetMode(payload);

        if (mode == "detail")
        {
            var proId = payload.GetProperty("proId").GetInt32();
            await using var conn = await OpenAsync(ct);

            const string sql = @"
                SELECT pro_id, nombre, alias, representante, domicilio, noExt, noInt,
                       localidad, ciudad, estado, pais, codigoPostal, colonia, rfc,
                       curp, telefono, celular, mail, comentario, status, limite, diasCredito
                FROM proveedor
                WHERE pro_id = @id";

            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", proId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"Proveedor {proId} no encontrado en SICAR");
            }

            return ReadRow(reader);
        }

        // mode == "list"
        var (page, limit, skip) = GetPagination(payload, defaultLimit: 50, maxLimit: 200);
        var search = GetOptionalString(payload, "search");

        await using var listConn = await OpenAsync(ct);

        var where = "WHERE status = 1";
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrEmpty(search))
        {
            where += " AND (nombre LIKE @search OR alias LIKE @search OR rfc LIKE @search)";
            parameters.Add(("@search", $"%{search}%"));
        }

        var items = new List<Dictionary<string, object?>>();
        var listSql = $@"
            SELECT pro_id, nombre, alias, representante, telefono, celular, mail,
                   ciudad, estado, rfc, limite, diasCredito, status
            FROM proveedor
            {where}
            ORDER BY nombre ASC
            LIMIT @limit OFFSET @skip";

        await using (var listCmd = new MySqlCommand(listSql, listConn))
        {
            foreach (var (name, value) in parameters)
            {
                listCmd.Parameters.AddWithValue(name, value);
            }
            listCmd.Parameters.AddWithValue("@limit", limit);
            listCmd.Parameters.AddWithValue("@skip", skip);
            await using var reader = await listCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadRow(reader));
            }
        }

        var countSql = $"SELECT COUNT(*) FROM proveedor {where}";
        long total;
        await using (var countCmd = new MySqlCommand(countSql, listConn))
        {
            foreach (var (name, value) in parameters)
            {
                countCmd.Parameters.AddWithValue(name, value);
            }
            var result = await countCmd.ExecuteScalarAsync(ct);
            total = result is null ? 0 : Convert.ToInt64(result);
        }

        return new { items, total, page, limit };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GetMode(JsonElement payload)
    {
        if (payload.TryGetProperty("mode", out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString() ?? "list";
        }
        return "list";
    }

    private static (int Page, int Limit, int Skip) GetPagination(
        JsonElement payload,
        int defaultLimit,
        int maxLimit)
    {
        var page = GetOptionalInt(payload, "page") ?? 1;
        var limit = Math.Min(GetOptionalInt(payload, "limit") ?? defaultLimit, maxLimit);
        if (page < 1) page = 1;
        if (limit < 1) limit = defaultLimit;
        var skip = (page - 1) * limit;
        return (page, limit, skip);
    }

    private static string? GetOptionalString(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }
        return null;
    }

    private static int? GetOptionalInt(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            return val.GetInt32();
        }
        return null;
    }

    private static bool? GetOptionalBool(JsonElement payload, string name)
    {
        if (payload.TryGetProperty(name, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }


    private static async Task<string> ScalarString(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? string.Empty;
    }

    private static async Task<long> ScalarLong(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt64(result);
    }

    private static Dictionary<string, object?> ReadRow(MySqlDataReader reader)
    {
        var row = new Dictionary<string, object?>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return row;
    }

    private static object ToSqlValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => DBNull.Value,
        _ => el.GetRawText(),
    };
}
