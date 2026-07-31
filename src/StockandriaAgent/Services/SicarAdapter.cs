using System.Text.Json;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace StockandriaAgent.Services;

/// <summary>
/// Implementación real de <see cref="ISicarAdapter"/> contra el servidor
/// MariaDB/MySQL local de SICAR. En el modelo multi-sucursal, el servidor
/// aloja varias DBs (una por sucursal: sicar_norte, sicar_chihuahua, etc.).
/// Cada comando incluye en su payload un campo `databaseName` que el adapter
/// usa para armar la connection string contra esa DB puntual.
///
/// Regla: SELECT, UPDATE, y un único INSERT permitido
/// (<see cref="InsertProductAsync"/>) para sincronizar productos creados
/// desde Stockandria. Nunca DELETE — los borrados se manejan vía soft-delete
/// en Stockandria, no se propagan.
/// </summary>
public class SicarAdapter : ISicarAdapter
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema",
        "mysql",
        "performance_schema",
        "sys",
    };

    private readonly AgentSession _session;
    private readonly ILogger<SicarAdapter> _logger;

    public SicarAdapter(AgentSession session, ILogger<SicarAdapter> logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Abre conexión contra el servidor MySQL. Si se pasa <paramref name="databaseName"/>,
    /// concatena `;Database={databaseName}` a la base connection string.
    /// </summary>
    private async Task<MySqlConnection> OpenAsync(string? databaseName, CancellationToken ct)
    {
        var config = await _session.WaitForConfigAsync(ct);
        if (string.IsNullOrWhiteSpace(config.SicarBaseConnectionString))
        {
            throw new InvalidOperationException(
                "SicarBaseConnectionString no está configurada. Ejecutar el wizard del agente.");
        }

        var cs = config.SicarBaseConnectionString;
        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            cs = cs.TrimEnd(';') + $";Database={databaseName};";
        }

        var conn = new MySqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<SicarReachability> TestConnectionAsync(JsonElement? payload, CancellationToken ct)
    {
        try
        {
            var databaseName = payload is JsonElement p
                ? GetOptionalString(p, "databaseName")
                : null;

            await using var conn = await OpenAsync(databaseName, ct);
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

    public async Task<List<string>> ListDatabasesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(null, ct);
        await using var cmd = new MySqlCommand("SHOW DATABASES", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var databases = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!SystemDatabases.Contains(name))
            {
                databases.Add(name);
            }
        }
        return databases;
    }

    public async Task<object> GetStatusAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);

        var version = await ScalarString(conn, "SELECT VERSION()", ct);
        var articlesCount = await ScalarLong(conn, "SELECT COUNT(*) FROM articulo", ct);
        var suppliersCount = await ScalarLong(conn, "SELECT COUNT(*) FROM proveedor", ct);
        var departmentsCount = await ScalarLong(conn, "SELECT COUNT(*) FROM departamento", ct);

        return new
        {
            database = db,
            sicarVersion = version,
            articlesCount,
            suppliersCount,
            departmentsCount,
        };
    }

    public async Task<object> SyncProductsAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);
        var products = new List<Dictionary<string, object?>>();

        // Nota: la tabla `impuesto` viene vacía en los SICAR de prueba y la
        // columna del porcentaje varía según versión (`porcentaje`, `tasa`,
        // etc.). Devolvemos NULL para que el back aplique fallback 16%.
        const string sql = @"
            SELECT a.art_id, a.clave, a.claveAlterna, a.caracteristicas, a.descripcion,
                   a.precio1, a.precio2, a.precio3, a.precio4,
                   a.precioCompra, a.existencia, a.invMin, a.invMax,
                   a.cat_id, a.status,
                   c.nombre AS categoria_nombre,
                   u.nombre AS unidad_nombre,
                   NULL AS iva_porcentaje,
                   (SELECT pa.pro_id
                    FROM proveedorarticulo pa
                    WHERE pa.art_id = a.art_id
                    LIMIT 1) AS proveedor_pro_id
            FROM articulo a
            LEFT JOIN categoria c ON c.cat_id = a.cat_id
            LEFT JOIN unidad u ON u.uni_id = a.unidadVenta";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            products.Add(ReadRow(reader));
        }

        return new { database = db, syncedCount = products.Count, products };
    }

    public async Task<object> SyncStockAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);
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

        return new { database = db, syncedCount = stock.Count, stock };
    }

    public async Task<object> SyncSalesAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);

        // El sync de ventas SIEMPRE viene acotado por rango [from, to): el
        // backfill historico lo llama mes a mes y el delta diario con la ventana
        // del dia. Asi nunca se trae todo el historico de una sola vez.
        var from = payload.TryGetProperty("from", out var f) ? f.GetString() : null;
        var to = payload.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("SYNC_SALES requiere 'from' y 'to' (formato yyyy-MM-dd)");
        }

        await using var conn = await OpenAsync(db, ct);
        var sales = new List<Dictionary<string, object?>>();

        // Demanda real: solo ventas al cliente (status = 1) y se excluyen las
        // ventas por ajuste de inventario (ventaPorAjuste = 1). Se agrega por
        // clave (SKU) + dia en el propio SQL para no transferir las lineas
        // crudas. La sucursal es la base de datos, no hay columna de sucursal.
        const string sql = @"
            SELECT d.clave AS clave,
                   DATE(v.fecha) AS dia,
                   SUM(d.cantidad) AS unidades,
                   SUM(d.importeCon) AS ingreso,
                   SUM(d.importeCompra) AS costo
            FROM detallev d
            JOIN venta v ON v.ven_id = d.ven_id
            WHERE v.status = 1
              AND v.ventaPorAjuste = 0
              AND v.fecha >= @from
              AND v.fecha < @to
            GROUP BY d.clave, DATE(v.fecha)";

        await using (var cmd = new MySqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sales.Add(ReadRow(reader));
            }
        }

        // Ticket promedio (BTP): resumen de tickets por dia. Un ticket = una venta
        // (ven_id distinto). El total y la utilidad se calculan sobre las lineas
        // (importeCon / importeCon - importeCompra) para que reconcilien con las
        // ventas por producto. Mismos filtros (status = 1, sin ajustes) y mismo
        // rango. El reader anterior ya se cerro: MySQL no permite dos abiertos.
        var ticketSummary = new List<Dictionary<string, object?>>();
        const string ticketSql = @"
            SELECT DATE(v.fecha) AS dia,
                   COUNT(DISTINCT v.ven_id) AS tickets,
                   SUM(d.importeCon) AS total,
                   SUM(d.importeCon) - SUM(COALESCE(d.importeCompra, 0)) AS utilidad
            FROM detallev d
            JOIN venta v ON v.ven_id = d.ven_id
            WHERE v.status = 1
              AND v.ventaPorAjuste = 0
              AND v.fecha >= @from
              AND v.fecha < @to
            GROUP BY DATE(v.fecha)";

        await using (var cmdTicket = new MySqlCommand(ticketSql, conn))
        {
            cmdTicket.Parameters.AddWithValue("@from", from);
            cmdTicket.Parameters.AddWithValue("@to", to);
            await using var readerTicket = await cmdTicket.ExecuteReaderAsync(ct);
            while (await readerTicket.ReadAsync(ct))
            {
                ticketSummary.Add(ReadRow(readerTicket));
            }
        }

        // Venta por hora / pico horario: mismas ventas agregadas por dia + hora del
        // dia (0-23). Sirve para saber en que franjas se vende mas (abrir/cerrar).
        // Mismos filtros y rango. El reader anterior ya se cerro.
        var hourlySummary = new List<Dictionary<string, object?>>();
        const string hourlySql = @"
            SELECT DATE(v.fecha) AS dia,
                   HOUR(v.fecha) AS hora,
                   COUNT(DISTINCT v.ven_id) AS tickets,
                   SUM(d.importeCon) AS total
            FROM detallev d
            JOIN venta v ON v.ven_id = d.ven_id
            WHERE v.status = 1
              AND v.ventaPorAjuste = 0
              AND v.fecha >= @from
              AND v.fecha < @to
            GROUP BY DATE(v.fecha), HOUR(v.fecha)";

        await using (var cmdHourly = new MySqlCommand(hourlySql, conn))
        {
            cmdHourly.Parameters.AddWithValue("@from", from);
            cmdHourly.Parameters.AddWithValue("@to", to);
            await using var readerHourly = await cmdHourly.ExecuteReaderAsync(ct);
            while (await readerHourly.ReadAsync(ct))
            {
                hourlySummary.Add(ReadRow(readerHourly));
            }
        }

        // Ventas por VENDEDOR (meet-24 / reportes.md sec. 5): quien hizo la venta.
        // En SICAR el vendedor cuelga de la venta (venta.vnd_id -> vendedor). Se
        // traen dos agregaciones porque responden cosas distintas:
        //
        //  - sellerSummary (dia + vendedor): tickets, venta y utilidad. Los
        //    tickets NO se pueden sumar desde el detalle por producto (un mismo
        //    ticket tiene varias lineas), por eso va aparte.
        //  - productSellerSummary (dia + producto + vendedor): quien vendio cada
        //    producto, para el historial de movimientos.
        //
        // Las ventas sin vendedor (vnd_id NULL) se traen igual con vendedor 0 /
        // nombre vacio: son ventas reales y no se pueden perder.
        var sellerSummary = new List<Dictionary<string, object?>>();
        const string sellerSql = @"
            SELECT DATE(v.fecha) AS dia,
                   COALESCE(v.vnd_id, 0) AS vendedorId,
                   COALESCE(vd.nombre, '') AS vendedor,
                   COUNT(DISTINCT v.ven_id) AS tickets,
                   SUM(d.importeCon) AS total,
                   SUM(d.importeCon) - SUM(COALESCE(d.importeCompra, 0)) AS utilidad,
                   SUM(d.cantidad) AS unidades
            FROM detallev d
            JOIN venta v ON v.ven_id = d.ven_id
            LEFT JOIN vendedor vd ON vd.vnd_id = v.vnd_id
            WHERE v.status = 1
              AND v.ventaPorAjuste = 0
              AND v.fecha >= @from
              AND v.fecha < @to
            GROUP BY DATE(v.fecha), COALESCE(v.vnd_id, 0), COALESCE(vd.nombre, '')";

        await using (var cmdSeller = new MySqlCommand(sellerSql, conn))
        {
            cmdSeller.Parameters.AddWithValue("@from", from);
            cmdSeller.Parameters.AddWithValue("@to", to);
            await using var readerSeller = await cmdSeller.ExecuteReaderAsync(ct);
            while (await readerSeller.ReadAsync(ct))
            {
                sellerSummary.Add(ReadRow(readerSeller));
            }
        }

        var productSellerSummary = new List<Dictionary<string, object?>>();
        const string productSellerSql = @"
            SELECT d.clave AS clave,
                   DATE(v.fecha) AS dia,
                   COALESCE(v.vnd_id, 0) AS vendedorId,
                   COALESCE(vd.nombre, '') AS vendedor,
                   SUM(d.cantidad) AS unidades,
                   SUM(d.importeCon) AS ingreso,
                   SUM(d.importeCompra) AS costo
            FROM detallev d
            JOIN venta v ON v.ven_id = d.ven_id
            LEFT JOIN vendedor vd ON vd.vnd_id = v.vnd_id
            WHERE v.status = 1
              AND v.ventaPorAjuste = 0
              AND v.fecha >= @from
              AND v.fecha < @to
            GROUP BY d.clave, DATE(v.fecha), COALESCE(v.vnd_id, 0), COALESCE(vd.nombre, '')";

        await using (var cmdProductSeller = new MySqlCommand(productSellerSql, conn))
        {
            cmdProductSeller.Parameters.AddWithValue("@from", from);
            cmdProductSeller.Parameters.AddWithValue("@to", to);
            await using var readerProductSeller = await cmdProductSeller.ExecuteReaderAsync(ct);
            while (await readerProductSeller.ReadAsync(ct))
            {
                productSellerSummary.Add(ReadRow(readerProductSeller));
            }
        }

        _logger.LogInformation(
            "SYNC_SALES db={Db} from={From} to={To} rows={Rows} ticketDays={TicketDays} hourlyRows={HourlyRows} sellerRows={SellerRows} productSellerRows={ProductSellerRows}",
            db, from, to, sales.Count, ticketSummary.Count, hourlySummary.Count,
            sellerSummary.Count, productSellerSummary.Count);
        return new
        {
            database = db,
            from,
            to,
            syncedCount = sales.Count,
            sales,
            ticketSummary,
            hourlySummary,
            sellerSummary,
            productSellerSummary,
        };
    }

    public async Task<object> SyncStockHistoryAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);

        var from = payload.TryGetProperty("from", out var f) ? f.GetString() : null;
        var to = payload.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("SYNC_STOCK_HISTORY requiere 'from' y 'to' (formato yyyy-MM-dd)");
        }

        await using var conn = await OpenAsync(db, ct);
        var changes = new List<Dictionary<string, object?>>();

        // Estrategia comprimida: SICAR guarda una foto diaria de la existencia de
        // cada articulo (inventariofecha tipo=1 + inventariofechaarticulo). En vez
        // de transferir la foto completa, se emite una fila SOLO cuando la
        // existencia cambia respecto al dia anterior (LAG). El rango arranca desde
        // la foto inmediatamente anterior a @from (el ancla) para que el primer dia
        // del rango se compare contra su valor real y no genere un cambio falso;
        // luego se filtran las filas para devolver solo las que caen dentro de
        // [from, to). Esto tambien hace funcionar el delta diario (1 solo dia).
        const string sql = @"
            SELECT clave, dia, existencia FROM (
                SELECT ifa.clave AS clave,
                       DATE(f.fecha) AS dia,
                       f.fecha AS ts,
                       ifa.existencia AS existencia,
                       LAG(ifa.existencia) OVER (PARTITION BY ifa.clave ORDER BY f.fecha) AS prev
                FROM inventariofechaarticulo ifa
                JOIN inventariofecha f ON f.inf_id = ifa.inf_id
                WHERE f.tipo = 1
                  AND f.fecha >= COALESCE(
                      (SELECT MAX(f2.fecha) FROM inventariofecha f2 WHERE f2.tipo = 1 AND f2.fecha < @from),
                      @from)
                  AND f.fecha < @to
            ) t
            WHERE t.ts >= @from AND (t.prev IS NULL OR t.existencia <> t.prev)
            ORDER BY clave, dia";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            changes.Add(ReadRow(reader));
        }

        _logger.LogInformation(
            "SYNC_STOCK_HISTORY db={Db} from={From} to={To} rows={Rows}", db, from, to, changes.Count);
        return new { database = db, from, to, syncedCount = changes.Count, changes };
    }

    public async Task<object> SyncPurchaseHistoryAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);

        var from = payload.TryGetProperty("from", out var f) ? f.GetString() : null;
        var to = payload.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            throw new InvalidOperationException("SYNC_PURCHASE_HISTORY requiere 'from' y 'to' (formato yyyy-MM-dd)");
        }

        await using var conn = await OpenAsync(db, ct);
        var purchases = new List<Dictionary<string, object?>>();

        // Historial de costos: una fila por articulo y dia con compra, con el
        // costo por PIEZA de ese dia. detallec guarda cantidad en unidad de
        // compra y factor piezas-por-unidad, asi que el costo por pieza sale de
        // importeSin / (cantidad * factor): robusto aunque se compre por caja.
        // Si el mismo dia hubo varias compras del articulo se promedia ponderado
        // (SUM importe / SUM piezas). Solo compras aplicadas (status = 1).
        const string sql = @"
            SELECT dc.clave AS clave,
                   DATE(c.fecha) AS dia,
                   SUM(dc.importeSin) AS importe,
                   SUM(dc.cantidad * dc.factor) AS piezas
            FROM compra c
            JOIN detallec dc ON dc.com_id = c.com_id
            WHERE c.status = 1
              AND c.fecha >= @from AND c.fecha < @to
              AND dc.cantidad > 0
            GROUP BY dc.clave, DATE(c.fecha)
            HAVING SUM(dc.cantidad * dc.factor) > 0
            ORDER BY clave, dia";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            purchases.Add(ReadRow(reader));
        }

        _logger.LogInformation(
            "SYNC_PURCHASE_HISTORY db={Db} from={From} to={To} rows={Rows}", db, from, to, purchases.Count);
        return new { database = db, from, to, syncedCount = purchases.Count, purchases };
    }

    public async Task<object> SyncSuppliersAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);
        var suppliers = new List<Dictionary<string, object?>>();

        const string sql = @"
            SELECT pro_id,
                   nombre,
                   representante,
                   alias,
                   rfc,
                   domicilio AS direccion,
                   noExt,
                   noInt,
                   colonia,
                   localidad,
                   ciudad,
                   estado,
                   pais,
                   codigoPostal,
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

        return new { database = db, syncedCount = suppliers.Count, suppliers };
    }

    public Task<object> CreateBackupAsync(JsonElement payload, CancellationToken ct)
    {
        // TODO: implementar con mysqldump o equivalente + presigned URL.
        // El db en cuestión está en payload.databaseName.
        throw new NotImplementedException("CreateBackup todavía no está implementado");
    }

    // -------------------------------------------------------------------------
    // Operaciones de escritura — SOLO UPDATE. Nunca INSERT ni DELETE.
    // -------------------------------------------------------------------------

    public async Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var newStock = payload.GetProperty("newStock").GetInt32();

        await using var conn = await OpenAsync(db, ct);
        var artId = await ResolveArtIdAsync(payload, conn, ct);
        await using var cmd = new MySqlCommand(
            "UPDATE articulo SET existencia = @stock WHERE art_id = @id",
            conn);
        cmd.Parameters.AddWithValue("@stock", newStock);
        cmd.Parameters.AddWithValue("@id", artId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("ADJUST_STOCK db={Db} art_id={ArtId} newStock={NewStock} rows={Rows}",
            db, artId, newStock, rows);

        return new { artId, newStock, rowsAffected = rows };
    }

    public async Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var adjustments = payload.GetProperty("adjustments");
        if (adjustments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("payload.adjustments debe ser un array");
        }

        await using var conn = await OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var results = new List<object>();
        try
        {
            foreach (var adj in adjustments.EnumerateArray())
            {
                var artId = await ResolveArtIdAsync(adj, conn, ct, tx);
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
        var db = RequireDatabaseName(payload);
        var prices = payload.GetProperty("prices");

        // Recolectar los precios enviados (precio1-4 + precioCompra).
        var enviados = new Dictionary<string, decimal>();
        foreach (var prop in new[] { "precio1", "precio2", "precio3", "precio4", "precioCompra" })
        {
            if (prices.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
            {
                enviados[prop] = val.GetDecimal();
            }
        }

        if (enviados.Count == 0)
        {
            throw new InvalidOperationException("No se envió ningún precio para actualizar");
        }

        await using var conn = await OpenAsync(db, ct);
        var artId = await ResolveArtIdAsync(payload, conn, ct);

        // SICAR guarda margen1-4 (utilidad) por artículo y NO los recalcula solo
        // al cambiar el precio. Para que no queden desactualizados los mantenemos
        // consistentes acá con la misma fórmula que usa Stockandria
        // (precio = costo * (1 + margen/100)  =>  margen = (precio/costo - 1) * 100).
        // El costo es el precioCompra enviado en este mismo update; si no vino,
        // se lee el actual del artículo.
        decimal? costo = enviados.TryGetValue("precioCompra", out var pc) ? pc : null;
        if (costo is null)
        {
            await using var costCmd = new MySqlCommand(
                "SELECT precioCompra FROM articulo WHERE art_id = @id", conn);
            costCmd.Parameters.AddWithValue("@id", artId);
            var costResult = await costCmd.ExecuteScalarAsync(ct);
            if (costResult is not null && costResult != DBNull.Value)
            {
                costo = Convert.ToDecimal(costResult);
            }
        }

        var sets = new List<string>();
        var cmd = new MySqlCommand();
        foreach (var kv in enviados)
        {
            sets.Add($"{kv.Key} = @{kv.Key}");
            cmd.Parameters.AddWithValue($"@{kv.Key}", kv.Value);
        }

        // Recalcular el margen de cada precio de venta enviado (solo si hay costo > 0).
        if (costo is > 0m)
        {
            var niveles = new[]
            {
                ("precio1", "margen1"),
                ("precio2", "margen2"),
                ("precio3", "margen3"),
                ("precio4", "margen4"),
            };
            foreach (var (precioProp, margenProp) in niveles)
            {
                if (enviados.TryGetValue(precioProp, out var precioVal))
                {
                    var margen = (precioVal / costo.Value - 1m) * 100m;
                    sets.Add($"{margenProp} = @{margenProp}");
                    cmd.Parameters.AddWithValue($"@{margenProp}", margen);
                }
            }
        }

        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
        cmd.Parameters.AddWithValue("@id", artId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_PRICE db={Db} art_id={ArtId} rows={Rows}", db, artId, rows);
        return new { artId, rowsAffected = rows };
    }

    public async Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
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

        await using var conn = await OpenAsync(db, ct);
        var artId = await ResolveArtIdAsync(payload, conn, ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
        cmd.Parameters.AddWithValue("@id", artId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_MIN_MAX db={Db} art_id={ArtId} rows={Rows}", db, artId, rows);
        return new { artId, rowsAffected = rows };
    }

    public async Task<object> BulkUpdateMinMaxAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var items = payload.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("payload.items debe ser un array");
        }

        await using var conn = await OpenAsync(db, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var results = new List<object>();
        try
        {
            foreach (var item in items.EnumerateArray())
            {
                // Cada item se ubica por `clave` (SKU) o `artId` — igual que el
                // resto de los comandos de escritura (ver ResolveArtIdAsync).
                var artId = await ResolveArtIdAsync(item, conn, ct, tx);

                var sets = new List<string>();
                await using var cmd = new MySqlCommand { Connection = conn, Transaction = tx };
                if (item.TryGetProperty("invMin", out var min) && min.ValueKind == JsonValueKind.Number)
                {
                    sets.Add("invMin = @invMin");
                    cmd.Parameters.AddWithValue("@invMin", min.GetInt32());
                }
                if (item.TryGetProperty("invMax", out var max) && max.ValueKind == JsonValueKind.Number)
                {
                    sets.Add("invMax = @invMax");
                    cmd.Parameters.AddWithValue("@invMax", max.GetInt32());
                }
                if (sets.Count == 0)
                {
                    continue;
                }
                cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
                cmd.Parameters.AddWithValue("@id", artId);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                results.Add(new { artId, rowsAffected = rows });
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation("BULK_UPDATE_MIN_MAX db={Db} items={Count}", db, results.Count);
        return new { results };
    }

    public async Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
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

        await using var conn = await OpenAsync(db, ct);
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
            "TRANSFER_STOCK db={Db} ({Direction}) con {Count} items", db, direction, results.Count);
        return new { direction, results };
    }

    public async Task<object> UpdateProductAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var fields = payload.GetProperty("fields");

        // Whitelist: solo permitimos modificar campos comunes con Stockandria
        // (descripcion=name, clave=sku, claveAlterna=barcode, status=isActive,
        // caracteristicas=códigos extra). Los precios/stock/min-max tienen sus
        // propios comandos (UPDATE_PRICE, ADJUST_STOCK, UPDATE_MIN_MAX) - no van por acá.
        var allowed = new[] { "descripcion", "clave", "claveAlterna", "caracteristicas", "status" };
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

        await using var conn = await OpenAsync(db, ct);
        var artId = await ResolveArtIdAsync(payload, conn, ct);

        // Reasignación de categoría: Stockandria manda el NOMBRE (no el cat_id),
        // porque cada DB SICAR tiene su propio cat_id para la misma categoría.
        // Resolvemos el cat_id por nombre en ESTA base. Si no existe, fallamos
        // (Stockandria debe sincronizar la categoría antes).
        if (fields.TryGetProperty("categoria", out var catEl)
            && catEl.ValueKind == JsonValueKind.String)
        {
            var categoriaNombre = catEl.GetString() ?? "";
            await using var catCmd = new MySqlCommand(
                "SELECT cat_id FROM categoria WHERE nombre = @nombre LIMIT 1", conn);
            catCmd.Parameters.AddWithValue("@nombre", categoriaNombre);
            var catResult = await catCmd.ExecuteScalarAsync(ct);
            if (catResult == null || catResult == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"La categoría \"{categoriaNombre}\" no existe en SICAR. " +
                    "Sincronizá categorías desde Stockandria antes de reasignar el producto.");
            }
            sets.Add("cat_id = @catId");
            cmd.Parameters.AddWithValue("@catId", Convert.ToInt32(catResult));
        }

        if (sets.Count == 0)
        {
            throw new InvalidOperationException("No se envió ningún campo para actualizar");
        }

        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
        cmd.Parameters.AddWithValue("@id", artId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_PRODUCT db={Db} art_id={ArtId} rows={Rows}", db, artId, rows);
        return new { artId, rowsAffected = rows };
    }

    /// <summary>
    /// Inserta un producto nuevo en la tabla `articulo` y devuelve el
    /// `art_id` generado por MySQL (LAST_INSERT_ID).
    ///
    /// Payload requerido:
    ///   clave           (string, único)
    ///   descripcion     (string, nombre del producto)
    ///   precio          (decimal, precio de venta — se asigna a precio1)
    ///   precioCompra    (decimal, costo)
    ///   categoria       (string, nombre — el agente resuelve cat_id)
    ///   unidad          (string, nombre — el agente resuelve unidadCompra
    ///                    y unidadVenta; ambas terminan siendo la misma)
    ///
    /// Payload opcional:
    ///   claveAlterna, invMin, invMax
    ///
    /// El agente resuelve cat_id y uni_id buscando por nombre. Si no
    /// existe la categoría o unidad, falla con error claro: Stockandria
    /// debe asegurar que estén sincronizadas antes de encolar.
    ///
    /// Si la `clave` ya existe se aborta con error claro (Stockandria valida
    /// antes pero defendemos profundo). El art_id devuelto se guarda como
    /// sicar_code en Stockandria para que después UPDATE_PRODUCT/PRICE/etc
    /// puedan apuntar a él.
    /// </summary>
    public async Task<object> InsertProductAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var clave = payload.GetProperty("clave").GetString()
            ?? throw new InvalidOperationException("payload.clave es requerido");
        var descripcion = payload.GetProperty("descripcion").GetString()
            ?? throw new InvalidOperationException("payload.descripcion es requerido");
        var precio = payload.GetProperty("precio").GetDecimal();
        var precioCompra = payload.TryGetProperty("precioCompra", out var pc)
            && pc.ValueKind == JsonValueKind.Number
                ? pc.GetDecimal()
                : 0m;
        var categoriaNombre = payload.GetProperty("categoria").GetString()
            ?? throw new InvalidOperationException("payload.categoria es requerido");
        var unidadNombre = payload.GetProperty("unidad").GetString()
            ?? throw new InvalidOperationException("payload.unidad es requerido");

        var claveAlterna = payload.TryGetProperty("claveAlterna", out var ca)
            && ca.ValueKind == JsonValueKind.String
                ? ca.GetString() ?? ""
                : "";
        // Códigos extra del producto (columna caracteristicas en SICAR).
        var caracteristicas = payload.TryGetProperty("caracteristicas", out var car)
            && car.ValueKind == JsonValueKind.String
                ? car.GetString() ?? ""
                : "";
        var invMin = payload.TryGetProperty("invMin", out var mn)
            && mn.ValueKind == JsonValueKind.Number
                ? mn.GetInt32()
                : 0;
        var invMax = payload.TryGetProperty("invMax", out var mx)
            && mx.ValueKind == JsonValueKind.Number
                ? mx.GetInt32()
                : 0;

        await using var conn = await OpenAsync(db, ct);

        // Resolver cat_id y uni_id por nombre. Si no existen, fallar con
        // mensaje claro (Stockandria debe sincronizar antes).
        int catId;
        await using (var catCmd = new MySqlCommand(
            "SELECT cat_id FROM categoria WHERE nombre = @nombre LIMIT 1", conn))
        {
            catCmd.Parameters.AddWithValue("@nombre", categoriaNombre);
            var catResult = await catCmd.ExecuteScalarAsync(ct);
            if (catResult == null || catResult == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"La categoría \"{categoriaNombre}\" no existe en SICAR. " +
                    "Sincronizá categorías desde Stockandria antes de crear el producto.");
            }
            catId = Convert.ToInt32(catResult);
        }

        int uniId;
        await using (var uniCmd = new MySqlCommand(
            "SELECT uni_id FROM unidad WHERE nombre = @nombre LIMIT 1", conn))
        {
            uniCmd.Parameters.AddWithValue("@nombre", unidadNombre);
            var uniResult = await uniCmd.ExecuteScalarAsync(ct);
            if (uniResult == null || uniResult == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"La unidad \"{unidadNombre}\" no existe en SICAR. " +
                    "Las unidades validas son las del catalogo (PZA, KG, LT, ML, PAQ, etc.).");
            }
            uniId = Convert.ToInt32(uniResult);
        }

        // Validar que la clave no exista (defensa profunda — Stockandria
        // ya valida antes de encolar, pero un INSERT contra UNIQUE constraint
        // tira un error feo de MySQL).
        await using (var check = new MySqlCommand(
            "SELECT art_id FROM articulo WHERE clave = @clave LIMIT 1", conn))
        {
            check.Parameters.AddWithValue("@clave", clave);
            var existing = await check.ExecuteScalarAsync(ct);
            if (existing != null)
            {
                throw new InvalidOperationException(
                    $"Ya existe un artículo con clave \"{clave}\" en SICAR (art_id={existing}).");
            }
        }

        // Defaults conservadores: el editor extendido de Stockandria luego
        // ajusta margen/mayoreo via UPDATE_PRICE.
        const string sql = @"
            INSERT INTO articulo (
                clave, claveAlterna, descripcion, servicio, localizacion,
                invMin, invMax, factor, precioCompra, preCompraProm,
                margen1, margen2, margen3, margen4,
                precio1, precio2, precio3, precio4,
                mayoreo1, mayoreo2, mayoreo3, mayoreo4,
                existencia, caracteristicas, cuentaPredial,
                status, unidadCompra, unidadVenta, cat_id
            ) VALUES (
                @clave, @claveAlterna, @descripcion, FALSE, '',
                @invMin, @invMax, 1.000, @precioCompra, @precioCompra,
                0.000000, 0.000000, 0.000000, 0.000000,
                @precio, 0.000000, 0.000000, 0.000000,
                0.000, 0.000, 0.000, 0.000,
                0.0000, @caracteristicas, '',
                1, @unidadCompra, @unidadVenta, @catId
            );
            SELECT LAST_INSERT_ID();";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@clave", clave);
        cmd.Parameters.AddWithValue("@claveAlterna", claveAlterna);
        cmd.Parameters.AddWithValue("@caracteristicas", caracteristicas);
        cmd.Parameters.AddWithValue("@descripcion", descripcion);
        cmd.Parameters.AddWithValue("@invMin", invMin);
        cmd.Parameters.AddWithValue("@invMax", invMax);
        cmd.Parameters.AddWithValue("@precioCompra", precioCompra);
        cmd.Parameters.AddWithValue("@precio", precio);
        // Unidad de compra y venta son la misma — la mayoría de los
        // productos del rubro se compran y venden en la misma unidad.
        cmd.Parameters.AddWithValue("@unidadCompra", uniId);
        cmd.Parameters.AddWithValue("@unidadVenta", uniId);
        cmd.Parameters.AddWithValue("@catId", catId);

        var artIdObj = await cmd.ExecuteScalarAsync(ct);
        if (artIdObj == null || artIdObj == DBNull.Value)
        {
            throw new InvalidOperationException(
                "INSERT_PRODUCT: no se obtuvo art_id del INSERT (LAST_INSERT_ID devolvió null).");
        }
        var artId = Convert.ToInt32(artIdObj);

        _logger.LogInformation(
            "INSERT_PRODUCT db={Db} art_id={ArtId} clave={Clave}", db, artId, clave);
        return new { artId, clave };
    }

    /// <summary>
    /// UPDATE de precios MASIVO: actualiza en UNA pasada los precios (precio1-4 +
    /// precioCompra) de muchos artículos. Resuelve cada uno por `clave`, recalcula
    /// margen1-4 = (precio/costo - 1) * 100 (igual que UpdatePriceAsync). Resiliente
    /// por item: el que falle (no existe, etc.) se reporta y sigue. Evita el flood
    /// de un UPDATE_PRICE por producto.
    ///
    /// Payload: { databaseName, items: [{ clave, precio1?, precio2?, precio3?,
    ///   precio4?, precioCompra? }] }
    /// Devuelve: { updated, failed: [{ clave, reason }] }
    /// </summary>
    public async Task<object> BulkUpdatePriceAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        if (!payload.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("payload.items (array) es requerido");
        }

        await using var conn = await OpenAsync(db, ct);

        var updated = 0;
        var failed = new List<object>();
        var priceProps = new[] { "precio1", "precio2", "precio3", "precio4", "precioCompra" };
        var nivelMargen = new[]
        {
            ("precio1", "margen1"),
            ("precio2", "margen2"),
            ("precio3", "margen3"),
            ("precio4", "margen4"),
        };

        foreach (var item in items.EnumerateArray())
        {
            var clave = GetOptionalString(item, "clave") ?? "";
            if (string.IsNullOrWhiteSpace(clave))
            {
                failed.Add(new { clave, reason = "clave vacía" });
                continue;
            }

            try
            {
                var enviados = new Dictionary<string, decimal>();
                foreach (var prop in priceProps)
                {
                    if (item.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.Number)
                    {
                        enviados[prop] = val.GetDecimal();
                    }
                }
                if (enviados.Count == 0)
                {
                    failed.Add(new { clave, reason = "sin precios para actualizar" });
                    continue;
                }

                // Resolver art_id + costo actual por clave.
                int artId;
                decimal? costoActual = null;
                await using (var idCmd = new MySqlCommand(
                    "SELECT art_id, precioCompra FROM articulo WHERE clave = @c LIMIT 1", conn))
                {
                    idCmd.Parameters.AddWithValue("@c", clave);
                    await using var rdr = await idCmd.ExecuteReaderAsync(ct);
                    if (!await rdr.ReadAsync(ct))
                    {
                        failed.Add(new { clave, reason = $"El artículo con clave \"{clave}\" no existe en SICAR" });
                        continue;
                    }
                    artId = rdr.GetInt32(0);
                    if (!rdr.IsDBNull(1)) costoActual = rdr.GetDecimal(1);
                }

                var costo = enviados.TryGetValue("precioCompra", out var pc) ? pc : costoActual;

                var sets = new List<string>();
                var cmd = new MySqlCommand();
                foreach (var kv in enviados)
                {
                    sets.Add($"{kv.Key} = @{kv.Key}");
                    cmd.Parameters.AddWithValue($"@{kv.Key}", kv.Value);
                }
                if (costo is > 0m)
                {
                    foreach (var (precioProp, margenProp) in nivelMargen)
                    {
                        if (enviados.TryGetValue(precioProp, out var precioVal))
                        {
                            var margen = (precioVal / costo.Value - 1m) * 100m;
                            sets.Add($"{margenProp} = @{margenProp}");
                            cmd.Parameters.AddWithValue($"@{margenProp}", margen);
                        }
                    }
                }

                cmd.Connection = conn;
                cmd.CommandText = $"UPDATE articulo SET {string.Join(", ", sets)} WHERE art_id = @id";
                cmd.Parameters.AddWithValue("@id", artId);
                await cmd.ExecuteNonQueryAsync(ct);
                updated++;
            }
            catch (Exception ex)
            {
                failed.Add(new { clave, reason = ex.Message });
            }
        }

        _logger.LogInformation(
            "BULK_UPDATE_PRICE db={Db} updated={Updated} failed={Failed}", db, updated, failed.Count);
        return new { updated, failed };
    }

    /// <summary>
    /// INSERT masivo: inserta en UNA pasada todos los artículos del payload que
    /// NO existan ya en la DB (por `clave`). Una sola conexión, precarga las
    /// claves existentes y cachea cat_id/uni_id por nombre para no re-consultar.
    /// Resiliente por item: si uno falla (categoría/unidad inexistente, etc.) se
    /// reporta y sigue con el resto. Se usa en el backfill de productos faltantes.
    ///
    /// Payload: { databaseName, items: [{ clave, descripcion, precio,
    ///   precioCompra, categoria, unidad, claveAlterna }] }
    /// Devuelve: { inserted, skipped, failed: [{ clave, reason }] }
    /// </summary>
    public async Task<object> BulkInsertProductsAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        if (!payload.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("payload.items (array) es requerido");
        }

        await using var conn = await OpenAsync(db, ct);

        // Precargar las claves existentes: así saltamos duplicados sin un SELECT
        // por cada item (idempotencia barata).
        var existing = new HashSet<string>();
        await using (var clavesCmd = new MySqlCommand("SELECT clave FROM articulo", conn))
        await using (var reader = await clavesCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) existing.Add(reader.GetString(0));
            }
        }

        var catCache = new Dictionary<string, int?>();
        var uniCache = new Dictionary<string, int?>();

        async Task<int?> ResolveId(Dictionary<string, int?> cache, string table, string idCol, string nombre)
        {
            if (cache.TryGetValue(nombre, out var cached)) return cached;
            await using var cmd = new MySqlCommand(
                $"SELECT {idCol} FROM {table} WHERE nombre = @n LIMIT 1", conn);
            cmd.Parameters.AddWithValue("@n", nombre);
            var res = await cmd.ExecuteScalarAsync(ct);
            int? id = (res is null || res == DBNull.Value) ? null : Convert.ToInt32(res);
            cache[nombre] = id;
            return id;
        }

        const string insertSql = @"
            INSERT INTO articulo (
                clave, claveAlterna, descripcion, servicio, localizacion,
                invMin, invMax, factor, precioCompra, preCompraProm,
                margen1, margen2, margen3, margen4,
                precio1, precio2, precio3, precio4,
                mayoreo1, mayoreo2, mayoreo3, mayoreo4,
                existencia, caracteristicas, cuentaPredial,
                status, unidadCompra, unidadVenta, cat_id
            ) VALUES (
                @clave, @claveAlterna, @descripcion, FALSE, '',
                0, 0, 1.000, @precioCompra, @precioCompra,
                0.000000, 0.000000, 0.000000, 0.000000,
                @precio, 0.000000, 0.000000, 0.000000,
                0.000, 0.000, 0.000, 0.000,
                0.0000, '', '',
                1, @uni, @uni, @catId
            );";

        var inserted = 0;
        var skipped = 0;
        var failed = new List<object>();

        foreach (var item in items.EnumerateArray())
        {
            var clave = GetOptionalString(item, "clave") ?? "";
            if (string.IsNullOrWhiteSpace(clave)) { skipped++; continue; }
            if (existing.Contains(clave)) { skipped++; continue; }

            try
            {
                var descripcion = GetOptionalString(item, "descripcion") ?? clave;
                var categoria = GetOptionalString(item, "categoria") ?? "";
                var unidad = GetOptionalString(item, "unidad") ?? "PZA";
                var claveAlterna = GetOptionalString(item, "claveAlterna") ?? "";
                var precio = item.TryGetProperty("precio", out var pv) && pv.ValueKind == JsonValueKind.Number
                    ? pv.GetDecimal() : 0m;
                var precioCompra = item.TryGetProperty("precioCompra", out var pcv) && pcv.ValueKind == JsonValueKind.Number
                    ? pcv.GetDecimal() : 0m;

                var catId = await ResolveId(catCache, "categoria", "cat_id", categoria);
                if (catId is null)
                {
                    failed.Add(new { clave, reason = $"La categoría \"{categoria}\" no existe en SICAR" });
                    continue;
                }
                var uniId = await ResolveId(uniCache, "unidad", "uni_id", unidad);
                if (uniId is null)
                {
                    failed.Add(new { clave, reason = $"La unidad \"{unidad}\" no existe en SICAR" });
                    continue;
                }

                await using var cmd = new MySqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("@clave", clave);
                cmd.Parameters.AddWithValue("@claveAlterna", claveAlterna);
                cmd.Parameters.AddWithValue("@descripcion", descripcion);
                cmd.Parameters.AddWithValue("@precioCompra", precioCompra);
                cmd.Parameters.AddWithValue("@precio", precio);
                cmd.Parameters.AddWithValue("@uni", uniId.Value);
                cmd.Parameters.AddWithValue("@catId", catId.Value);
                await cmd.ExecuteNonQueryAsync(ct);

                inserted++;
                existing.Add(clave);
            }
            catch (Exception ex)
            {
                failed.Add(new { clave, reason = ex.Message });
            }
        }

        _logger.LogInformation(
            "BULK_INSERT_PRODUCT db={Db} inserted={Inserted} skipped={Skipped} failed={Failed}",
            db, inserted, skipped, failed.Count);
        return new { inserted, skipped, failed };
    }

    public async Task<object> InsertSupplierAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var fields = payload.GetProperty("fields");

        string Campo(string nombre)
        {
            if (fields.TryGetProperty(nombre, out var val) && val.ValueKind != JsonValueKind.Null)
            {
                return ToSqlValue(val)?.ToString() ?? "";
            }
            return "";
        }

        var nombre = Campo("nombre");
        if (string.IsNullOrWhiteSpace(nombre))
        {
            throw new InvalidOperationException("fields.nombre es requerido");
        }

        var diasCredito = 0;
        if (fields.TryGetProperty("diasCredito", out var dc) && dc.ValueKind == JsonValueKind.Number)
        {
            diasCredito = dc.GetInt32();
        }

        await using var conn = await OpenAsync(db, ct);

        // A diferencia de `departamento`, la tabla `proveedor` NO tiene el nombre
        // como UNIQUE: SICAR acepta dos proveedores con el mismo nombre. Se
        // chequea a mano para no duplicar cuando el backend reintenta.
        await using (var check = new MySqlCommand(
            "SELECT pro_id FROM proveedor WHERE nombre = @nombre AND status = 1 LIMIT 1", conn))
        {
            check.Parameters.AddWithValue("@nombre", nombre);
            var existente = await check.ExecuteScalarAsync(ct);
            if (existente != null)
            {
                return new { proId = Convert.ToInt32(existente), yaExistia = true };
            }
        }

        // Todas las columnas de `proveedor` son NOT NULL sin default (menos la
        // foto), asi que hay que mandarlas todas aunque vayan vacias.
        const string sql = @"
            INSERT INTO proveedor (
                nombre, representante, alias, domicilio, noExt, noInt,
                localidad, ciudad, estado, pais, codigoPostal, colonia,
                rfc, curp, telefono, celular, mail, comentario,
                status, limite, diasCredito
            ) VALUES (
                @nombre, @representante, @alias, @domicilio, @noExt, @noInt,
                @localidad, @ciudad, @estado, @pais, @codigoPostal, @colonia,
                @rfc, @curp, @telefono, @celular, @mail, @comentario,
                1, 0.00, @diasCredito
            );
            SELECT LAST_INSERT_ID();";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@representante", Campo("representante"));
        cmd.Parameters.AddWithValue("@alias", Campo("alias"));
        cmd.Parameters.AddWithValue("@domicilio", Campo("domicilio"));
        cmd.Parameters.AddWithValue("@noExt", Campo("noExt"));
        cmd.Parameters.AddWithValue("@noInt", Campo("noInt"));
        cmd.Parameters.AddWithValue("@localidad", Campo("localidad"));
        cmd.Parameters.AddWithValue("@ciudad", Campo("ciudad"));
        cmd.Parameters.AddWithValue("@estado", Campo("estado"));
        cmd.Parameters.AddWithValue("@pais", Campo("pais"));
        cmd.Parameters.AddWithValue("@codigoPostal", Campo("codigoPostal"));
        cmd.Parameters.AddWithValue("@colonia", Campo("colonia"));
        cmd.Parameters.AddWithValue("@rfc", Campo("rfc"));
        cmd.Parameters.AddWithValue("@curp", Campo("curp"));
        cmd.Parameters.AddWithValue("@telefono", Campo("telefono"));
        cmd.Parameters.AddWithValue("@celular", Campo("celular"));
        cmd.Parameters.AddWithValue("@mail", Campo("mail"));
        cmd.Parameters.AddWithValue("@comentario", Campo("comentario"));
        cmd.Parameters.AddWithValue("@diasCredito", diasCredito);

        var result = await cmd.ExecuteScalarAsync(ct);
        var proId = Convert.ToInt32(result);

        return new { proId, yaExistia = false };
    }

    public async Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var proId = payload.GetProperty("proId").GetInt32();
        var fields = payload.GetProperty("fields");

        // Nombres REALES de las columnas de `proveedor`. No hay `direccion` ni
        // `correo`: son `domicilio` y `mail`. Usar los nombres viejos hacia
        // fallar el UPDATE entero con "Unknown column".
        var allowed = new[] { "nombre", "alias", "rfc", "domicilio", "telefono", "mail", "diasCredito" };
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

        await using var conn = await OpenAsync(db, ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE proveedor SET {string.Join(", ", sets)} WHERE pro_id = @id";
        cmd.Parameters.AddWithValue("@id", proId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_SUPPLIER db={Db} pro_id={ProId} rows={Rows}", db, proId, rows);
        return new { proId, rowsAffected = rows };
    }

    // -------------------------------------------------------------------------
    // Operaciones de lectura — SOLO SELECT.
    // -------------------------------------------------------------------------

    public async Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var mode = GetMode(payload);

        if (mode == "detail")
        {
            var artId = payload.GetProperty("artId").GetInt32();
            await using var conn = await OpenAsync(db, ct);

            // Mismas columnas que SyncProductsAsync para que el back pueda
            // reusar ProductSyncerService.applySingle() en el flujo refresh.
            const string sql = @"
                SELECT a.art_id, a.clave, a.claveAlterna, a.descripcion,
                       a.precio1, a.precio2, a.precio3, a.precio4,
                       a.precioCompra, a.existencia, a.invMin, a.invMax,
                       a.cat_id, a.status,
                       a.localizacion, a.preCompraProm,
                       c.nombre AS categoria_nombre,
                       u.nombre AS unidad_nombre,
                       d.dep_id, d.nombre AS departamento_nombre,
                       NULL AS iva_porcentaje,
                       (SELECT pa.pro_id
                        FROM proveedorarticulo pa
                        WHERE pa.art_id = a.art_id
                        LIMIT 1) AS proveedor_pro_id
                FROM articulo a
                LEFT JOIN categoria c ON c.cat_id = a.cat_id
                LEFT JOIN unidad u ON u.uni_id = a.unidadVenta
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
            await using var conn = await OpenAsync(db, ct);
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

        await using var listConn = await OpenAsync(db, ct);

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
        var db = RequireDatabaseName(payload);
        var mode = GetMode(payload);

        if (mode == "detail")
        {
            var artId = payload.GetProperty("artId").GetInt32();
            await using var conn = await OpenAsync(db, ct);

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

        await using var listConn = await OpenAsync(db, ct);

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
        var db = RequireDatabaseName(payload);
        var (page, limit, skip) = GetPagination(payload, defaultLimit: 20, maxLimit: 100);
        var status = GetOptionalInt(payload, "status");

        await using var conn = await OpenAsync(db, ct);

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
        var db = RequireDatabaseName(payload);
        var mode = GetMode(payload);

        if (mode == "detail")
        {
            var proId = payload.GetProperty("proId").GetInt32();
            await using var conn = await OpenAsync(db, ct);

            // Aliases consistentes con SyncSuppliersAsync para que el back
            // pueda reusar SupplierSyncerService.applySingle() con esta fila
            // sin transformación intermedia.
            const string sql = @"
                SELECT pro_id, nombre, representante, alias, rfc,
                       domicilio AS direccion, noExt, noInt, colonia,
                       localidad, ciudad, estado, pais, codigoPostal,
                       telefono, mail AS correo,
                       diasCredito, status,
                       curp, celular, comentario, limite
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

        await using var listConn = await OpenAsync(db, ct);

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

    public async Task<object> GetProductMarginsAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);

        const string sql = @"
            SELECT cat_id, margen1, margen2, margen3, margen4
            FROM articulo
            WHERE status = 1 AND cat_id IS NOT NULL";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                catId = reader.GetInt32(reader.GetOrdinal("cat_id")),
                margen1 = reader.IsDBNull(reader.GetOrdinal("margen1")) ? 0m : reader.GetDecimal(reader.GetOrdinal("margen1")),
                margen2 = reader.IsDBNull(reader.GetOrdinal("margen2")) ? 0m : reader.GetDecimal(reader.GetOrdinal("margen2")),
                margen3 = reader.IsDBNull(reader.GetOrdinal("margen3")) ? 0m : reader.GetDecimal(reader.GetOrdinal("margen3")),
                margen4 = reader.IsDBNull(reader.GetOrdinal("margen4")) ? 0m : reader.GetDecimal(reader.GetOrdinal("margen4")),
            });
        }

        return rows;
    }

    public async Task<object> GetCategoriesAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);

        // JOIN categoria -> departamento: en SICAR la categoria pertenece a un
        // departamento (cat.dep_id). El proveedor NO esta en este eje, asi que
        // no se incluye: la carga inicial en Stockandria deriva el proveedor de
        // la categoria local que matchea por nombre.
        const string sql = @"
            SELECT c.cat_id, c.nombre AS categoria, d.dep_id, d.nombre AS departamento
            FROM categoria c
            JOIN departamento d ON d.dep_id = c.dep_id
            WHERE c.status = 1 AND d.status = 1
            ORDER BY d.nombre ASC, c.nombre ASC";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                catId = reader.GetInt32(reader.GetOrdinal("cat_id")),
                categoria = reader.IsDBNull(reader.GetOrdinal("categoria")) ? "" : reader.GetString(reader.GetOrdinal("categoria")),
                depId = reader.GetInt32(reader.GetOrdinal("dep_id")),
                departamento = reader.IsDBNull(reader.GetOrdinal("departamento")) ? "" : reader.GetString(reader.GetOrdinal("departamento")),
            });
        }

        return new { categories = rows };
    }

    public async Task<object> GetDepartmentsAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);

        // Sin JOIN contra categoria: el backend necesita ver tambien los
        // departamentos vacios para resolver su dep_id por nombre antes de
        // crear o renombrar. Cada DB SICAR es autoincrement independiente, asi
        // que el mismo departamento puede tener distinto dep_id por sucursal.
        const string sql = @"
            SELECT dep_id, nombre
            FROM departamento
            WHERE status = 1
            ORDER BY nombre ASC";

        await using var cmd = new MySqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                depId = reader.GetInt32(reader.GetOrdinal("dep_id")),
                nombre = reader.IsDBNull(reader.GetOrdinal("nombre")) ? "" : reader.GetString(reader.GetOrdinal("nombre")),
            });
        }

        return new { departments = rows };
    }

    public async Task<object> GetSupplierCategoriesAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);

        // En SICAR el departamento/categoria NO cuelgan del proveedor: el vinculo
        // es proveedor -> articulo (tabla puente proveedorarticulo) -> categoria
        // -> departamento. Inferimos los departamentos/categorias de un proveedor
        // a partir de los articulos que le compra. Si viene proId, filtra ese
        // proveedor; si no, trae el mapeo completo (un solo round-trip para el
        // sync masivo).
        var hasProId = payload.TryGetProperty("proId", out var proIdEl)
            && proIdEl.ValueKind == JsonValueKind.Number;

        var sql = @"
            SELECT DISTINCT pa.pro_id, d.dep_id, d.nombre AS departamento,
                            c.cat_id, c.nombre AS categoria
            FROM proveedorarticulo pa
            JOIN articulo a ON a.art_id = pa.art_id
            JOIN categoria c ON c.cat_id = a.cat_id
            JOIN departamento d ON d.dep_id = c.dep_id
            WHERE c.status = 1 AND d.status = 1";
        if (hasProId)
        {
            sql += " AND pa.pro_id = @proId";
        }
        sql += " ORDER BY pa.pro_id ASC, d.nombre ASC, c.nombre ASC";

        await using var cmd = new MySqlCommand(sql, conn);
        if (hasProId)
        {
            cmd.Parameters.AddWithValue("@proId", proIdEl.GetInt32());
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                proId = reader.GetInt32(reader.GetOrdinal("pro_id")),
                depId = reader.GetInt32(reader.GetOrdinal("dep_id")),
                departamento = reader.IsDBNull(reader.GetOrdinal("departamento")) ? "" : reader.GetString(reader.GetOrdinal("departamento")),
                catId = reader.GetInt32(reader.GetOrdinal("cat_id")),
                categoria = reader.IsDBNull(reader.GetOrdinal("categoria")) ? "" : reader.GetString(reader.GetOrdinal("categoria")),
            });
        }

        return new { rows };
    }

    public async Task<object> GetSupplierProductsAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        await using var conn = await OpenAsync(db, ct);

        // proveedorarticulo es la relacion muchos-a-muchos articulo <-> proveedor,
        // con precioCompra y fecha por cada proveedor. A diferencia de SyncProducts
        // (que toma LIMIT 1), aca traemos TODOS para poder comparar precios entre
        // proveedores. Devolvemos clave (sku) y art_id (sicarCode) para el match.
        var hasProId = payload.TryGetProperty("proId", out var proIdEl)
            && proIdEl.ValueKind == JsonValueKind.Number;

        var sql = @"
            SELECT pa.pro_id, pa.art_id, a.clave, pa.claveProveedor,
                   pa.precioCompra, pa.fecha
            FROM proveedorarticulo pa
            JOIN articulo a ON a.art_id = pa.art_id";
        if (hasProId)
        {
            sql += " WHERE pa.pro_id = @proId";
        }
        sql += " ORDER BY a.clave ASC, pa.pro_id ASC";

        await using var cmd = new MySqlCommand(sql, conn);
        if (hasProId)
        {
            cmd.Parameters.AddWithValue("@proId", proIdEl.GetInt32());
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                proId = reader.GetInt32(reader.GetOrdinal("pro_id")),
                artId = reader.GetInt32(reader.GetOrdinal("art_id")),
                clave = reader.IsDBNull(reader.GetOrdinal("clave")) ? "" : reader.GetString(reader.GetOrdinal("clave")),
                claveProveedor = reader.IsDBNull(reader.GetOrdinal("claveProveedor")) ? "" : reader.GetString(reader.GetOrdinal("claveProveedor")),
                precioCompra = reader.IsDBNull(reader.GetOrdinal("precioCompra")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("precioCompra")),
                fecha = reader.IsDBNull(reader.GetOrdinal("fecha")) ? (DateTime?)null : reader.GetDateTime(reader.GetOrdinal("fecha")),
            });
        }

        return new { rows };
    }

    public async Task<object> CreateDepartmentAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var nombre = payload.GetProperty("nombre").GetString()
            ?? throw new InvalidOperationException("payload.nombre es requerido");

        await using var conn = await OpenAsync(db, ct);

        // Stockandria garantiza nombre unico por proveedor, pero SICAR maneja
        // departamentos globales: evitamos duplicar uno que ya exista.
        await using (var check = new MySqlCommand(
            "SELECT dep_id FROM departamento WHERE nombre = @nombre LIMIT 1", conn))
        {
            check.Parameters.AddWithValue("@nombre", nombre);
            var existing = await check.ExecuteScalarAsync(ct);
            if (existing != null && existing != DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"Ya existe un departamento con nombre \"{nombre}\" en SICAR (dep_id={existing}).");
            }
        }

        const string sql = @"
            INSERT INTO departamento (nombre, restringido, porcentaje, `system`, status)
            VALUES (@nombre, FALSE, 0.00, FALSE, 1);
            SELECT LAST_INSERT_ID();";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        var depIdObj = await cmd.ExecuteScalarAsync(ct);
        if (depIdObj == null || depIdObj == DBNull.Value)
        {
            throw new InvalidOperationException(
                "CREATE_DEPARTMENT: no se obtuvo dep_id (LAST_INSERT_ID devolvió null).");
        }
        var depId = Convert.ToInt32(depIdObj);

        _logger.LogInformation(
            "CREATE_DEPARTMENT db={Db} dep_id={DepId} nombre={Nombre}", db, depId, nombre);
        return new { depId, nombre };
    }

    public async Task<object> UpdateDepartmentAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var depId = payload.GetProperty("depId").GetInt32();
        var fields = payload.GetProperty("fields");

        var allowed = new[] { "nombre" };
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

        await using var conn = await OpenAsync(db, ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE departamento SET {string.Join(", ", sets)} WHERE dep_id = @id";
        cmd.Parameters.AddWithValue("@id", depId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_DEPARTMENT db={Db} dep_id={DepId} rows={Rows}", db, depId, rows);
        return new { depId, rowsAffected = rows };
    }

    public async Task<object> CreateCategoryAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var nombre = payload.GetProperty("nombre").GetString()
            ?? throw new InvalidOperationException("payload.nombre es requerido");
        var depId = payload.GetProperty("depId").GetInt32();

        await using var conn = await OpenAsync(db, ct);

        // El departamento padre debe existir (cat.dep_id es NOT NULL). Si
        // Stockandria mando un dep_id inexistente, fallamos con mensaje claro.
        await using (var depCheck = new MySqlCommand(
            "SELECT dep_id FROM departamento WHERE dep_id = @depId LIMIT 1", conn))
        {
            depCheck.Parameters.AddWithValue("@depId", depId);
            var dep = await depCheck.ExecuteScalarAsync(ct);
            if (dep == null || dep == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"El departamento dep_id={depId} no existe en SICAR.");
            }
        }

        const string sql = @"
            INSERT INTO categoria (nombre, `system`, status, dep_id)
            VALUES (@nombre, FALSE, 1, @depId);
            SELECT LAST_INSERT_ID();";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@depId", depId);
        var catIdObj = await cmd.ExecuteScalarAsync(ct);
        if (catIdObj == null || catIdObj == DBNull.Value)
        {
            throw new InvalidOperationException(
                "CREATE_CATEGORY: no se obtuvo cat_id (LAST_INSERT_ID devolvió null).");
        }
        var catId = Convert.ToInt32(catIdObj);

        _logger.LogInformation(
            "CREATE_CATEGORY db={Db} cat_id={CatId} dep_id={DepId} nombre={Nombre}", db, catId, depId, nombre);
        return new { catId, depId, nombre };
    }

    public async Task<object> UpdateCategoryAsync(JsonElement payload, CancellationToken ct)
    {
        var db = RequireDatabaseName(payload);
        var catId = payload.GetProperty("catId").GetInt32();
        var fields = payload.GetProperty("fields");

        var allowed = new[] { "nombre" };
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

        await using var conn = await OpenAsync(db, ct);
        cmd.Connection = conn;
        cmd.CommandText = $"UPDATE categoria SET {string.Join(", ", sets)} WHERE cat_id = @id";
        cmd.Parameters.AddWithValue("@id", catId);

        var rows = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("UPDATE_CATEGORY db={Db} cat_id={CatId} rows={Rows}", db, catId, rows);
        return new { catId, rowsAffected = rows };
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resuelve el art_id del articulo a partir del payload. Cada DB SICAR (una
    /// por sucursal) asigna un art_id distinto al mismo producto, pero la `clave`
    /// (SKU) es identica en todas. Por eso, cuando el back hace broadcast a varias
    /// sucursales, manda la `clave` y el agente la traduce al art_id local de ESTA
    /// base.
    ///
    /// - Si el payload trae `clave` (string no vacio): resuelve el art_id por clave
    ///   en esta DB. Si no existe, falla con mensaje claro.
    /// - Si no trae `clave`: usa `artId` directo (comportamiento previo, compat).
    ///
    /// La conexion debe venir abierta por el caller. Si el caller esta dentro de
    /// una transaccion, debe pasarla en `tx`: MySqlConnector exige que el comando
    /// use la transaccion activa de la conexion (si no, lanza "The transaction
    /// associated with this command is not the connection's active transaction").
    /// </summary>
    private async Task<int> ResolveArtIdAsync(
        JsonElement payload, MySqlConnection conn, CancellationToken ct, MySqlTransaction? tx = null)
    {
        var clave = GetOptionalString(payload, "clave");
        if (!string.IsNullOrWhiteSpace(clave))
        {
            await using var cmd = new MySqlCommand(
                "SELECT art_id FROM articulo WHERE clave = @clave LIMIT 1", conn, tx);
            cmd.Parameters.AddWithValue("@clave", clave);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result == null || result == DBNull.Value)
            {
                throw new InvalidOperationException(
                    $"El artículo con clave \"{clave}\" no existe en SICAR.");
            }
            return Convert.ToInt32(result);
        }

        return payload.GetProperty("artId").GetInt32();
    }

    private static string RequireDatabaseName(JsonElement payload)
    {
        if (payload.TryGetProperty("databaseName", out var val) &&
            val.ValueKind == JsonValueKind.String)
        {
            var name = val.GetString();
            if (!string.IsNullOrWhiteSpace(name)) return name!;
        }
        throw new InvalidOperationException(
            "El payload no incluye 'databaseName'. La sucursal debe tener una DB SICAR vinculada en Stockandria.");
    }

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
