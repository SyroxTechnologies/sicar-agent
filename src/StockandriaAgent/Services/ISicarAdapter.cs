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

    /// <summary>
    /// Trae las ventas agregadas por clave (SKU) + dia desde SICAR, acotadas
    /// por rango [from, to) en el payload. Es el prerequisito del algoritmo de
    /// pronostico de demanda. Solo lectura. La sucursal es la base de datos.
    /// </summary>
    Task<object> SyncSalesAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Trae los cambios de existencia (estrategia comprimida) desde la foto
    /// diaria de inventario de SICAR, acotados por rango [from, to). Emite una
    /// fila solo cuando la existencia de un articulo cambia respecto al dia
    /// anterior. Es la curva de inventario para detectar el agotamiento real.
    /// Solo lectura. La sucursal es la base de datos.
    /// </summary>
    Task<object> SyncStockHistoryAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Historial de compras: costo por pieza por articulo y dia, desde la tabla
    /// compra/detallec de SICAR. Alimenta el costo vigente por fecha del GMROI.
    /// </summary>
    Task<object> SyncPurchaseHistoryAsync(JsonElement payload, CancellationToken ct);
    Task<object> SyncSuppliersAsync(JsonElement payload, CancellationToken ct);
    Task<object> CreateBackupAsync(JsonElement payload, CancellationToken ct);

    Task<object> AdjustStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> BulkAdjustStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdatePriceAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateMinMaxAsync(JsonElement payload, CancellationToken ct);
    Task<object> BulkUpdateMinMaxAsync(JsonElement payload, CancellationToken ct);
    Task<object> TransferStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateSupplierAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Da de alta un proveedor. Idempotente: si ya existe uno activo con ese
    /// nombre devuelve su pro_id sin insertar (la tabla `proveedor` no tiene el
    /// nombre como UNIQUE, asi que SICAR aceptaria el duplicado).
    /// </summary>
    Task<object> InsertSupplierAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateProductAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Inserta un producto nuevo en SICAR. Excepción autorizada a la regla
    /// "solo SELECT y UPDATE": cuando un usuario crea un producto desde
    /// Stockandria (típicamente desde el modal de regalo en una recepción
    /// con un artículo no registrado en ese proveedor), Stockandria es la
    /// fuente de verdad y necesita propagar el alta a SICAR para mantener
    /// las bases sincronizadas.
    /// </summary>
    Task<object> InsertProductAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// INSERT masivo de productos faltantes en una sucursal (backfill). Inserta
    /// solo los que no existan ya (por clave); resiliente por item.
    /// </summary>
    Task<object> BulkInsertProductsAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// UPDATE masivo de precios (precio1-4 + precioCompra) de muchos artículos en
    /// un solo comando, recalculando margen. Resiliente por item. Evita el flood
    /// de un UPDATE_PRICE por producto.
    /// </summary>
    Task<object> BulkUpdatePriceAsync(JsonElement payload, CancellationToken ct);

    Task<object> GetProductsAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetStockAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetTransfersAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetSuppliersAsync(JsonElement payload, CancellationToken ct);
    Task<object> GetProductMarginsAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Devuelve las categorias de SICAR con su departamento padre (JOIN
    /// categoria -> departamento). Usado por la carga inicial de departamentos
    /// en Stockandria: cada categoria local se matchea por nombre con esta
    /// lista para descubrir a que departamento pertenece.
    /// </summary>
    Task<object> GetCategoriesAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Devuelve los departamentos activos, incluidos los que todavia no tienen
    /// ninguna categoria. GET_CATEGORIES no sirve para eso porque parte de la
    /// tabla categoria y un departamento vacio nunca aparece en su JOIN.
    /// </summary>
    Task<object> GetDepartmentsAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Devuelve el mapeo proveedor -> departamento -> categoría inferido desde
    /// los artículos del proveedor (JOIN proveedorarticulo). Con `proId` en el
    /// payload filtra ese proveedor; sin él trae el mapeo completo. Usado por la
    /// sincronización viva: al sincronizar un proveedor se traen sus
    /// departamentos y categorías.
    /// </summary>
    Task<object> GetSupplierCategoriesAsync(JsonElement payload, CancellationToken ct);

    /// <summary>
    /// Devuelve el mapeo COMPLETO producto -> proveedor con precio de compra y
    /// fecha (tabla proveedorarticulo de SICAR), que es donde vive la relación
    /// muchos-a-muchos: un mismo artículo puede comprarse a varios proveedores,
    /// cada uno con su precioCompra. Sirve para la comparación "más barato por
    /// producto" en Stockandria. Con `proId` filtra ese proveedor; sin él trae
    /// todo. Devuelve clave (sku) y art_id (sicarCode) para el match.
    /// </summary>
    Task<object> GetSupplierProductsAsync(JsonElement payload, CancellationToken ct);

    // Escritura de catalogo (jerarquia departamento -> categoria). Los CREATE
    // devuelven el id generado por SICAR (dep_id / cat_id) para que Stockandria
    // lo guarde como sicarCode.
    Task<object> CreateDepartmentAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateDepartmentAsync(JsonElement payload, CancellationToken ct);
    Task<object> CreateCategoryAsync(JsonElement payload, CancellationToken ct);
    Task<object> UpdateCategoryAsync(JsonElement payload, CancellationToken ct);
}
