using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService.Payments;

/// <summary>
/// Mantiene el stock disponible de cada producto en SQLite. El catálogo embebido
/// define el stock inicial; a partir de ahí el stock se descuenta al aprobar una compra
/// y sobrevive a los reinicios de la aplicación.
/// </summary>
public sealed class CatalogStockService(
    ProductCatalog catalog,
    ThisContext database,
    ILogger<CatalogStockService> logger)
{
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        var existingIds = await database.ProductStocks
            .Select(stock => stock.ProductId)
            .ToListAsync(cancellationToken);

        var missing = catalog.Products
            .Where(product => !existingIds.Contains(product.Id))
            .Select(product => new ProductStock
            {
                ProductId = product.Id,
                Available = product.Stock,
                UpdatedAtUtc = DateTime.UtcNow
            })
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        database.ProductStocks.AddRange(missing);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        var availableByProduct = await database.ProductStocks
            .AsNoTracking()
            .ToDictionaryAsync(stock => stock.ProductId, stock => stock.Available, cancellationToken);

        return catalog.Products
            .Select(product => product with
            {
                Stock = availableByProduct.TryGetValue(product.Id, out var available)
                    ? available
                    : product.Stock
            })
            .ToArray();
    }

    public async Task<CatalogProduct?> GetProductAsync(int productId, CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetProduct(productId, out var product))
        {
            return null;
        }

        var available = await database.ProductStocks
            .AsNoTracking()
            .Where(stock => stock.ProductId == productId)
            .Select(stock => (int?)stock.Available)
            .SingleOrDefaultAsync(cancellationToken);

        return product with { Stock = available ?? product.Stock };
    }

    /// <summary>
    /// Descuenta el stock de una orden aprobada exactamente una vez. La marca
    /// <see cref="PaymentOrder.StockApplied"/> y el descuento se confirman dentro de
    /// la misma transacción, por lo que los webhooks repetidos no descuentan dos veces.
    /// </summary>
    public async Task<bool> TryApplyStockAsync(PaymentOrder order, CancellationToken cancellationToken = default)
    {
        if (order.StockApplied)
        {
            return true;
        }

        var items = order.Items.Count > 0
            ? order.Items
            : await database.Set<PaymentOrderItem>()
                .Where(item => item.PaymentOrderId == order.Id)
                .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            logger.LogWarning("Order {ExternalReference} has no items; stock was not discounted.", order.ExternalReference);
            return false;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "PaymentOrders"
            SET "StockApplied" = 1
            WHERE "Id" = {order.Id} AND "StockApplied" = 0
            """,
            cancellationToken);

        if (claimed == 0)
        {
            // Another webhook/verification already discounted this order.
            await transaction.CommitAsync(cancellationToken);
            order.StockApplied = true;
            return true;
        }

        foreach (var item in items)
        {
            var updated = await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE "ProductStocks"
                SET "Available" = "Available" - {item.Quantity}, "UpdatedAtUtc" = {DateTime.UtcNow}
                WHERE "ProductId" = {item.ProductId} AND "Available" >= {item.Quantity}
                """,
                cancellationToken);

            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                order.StockApplied = false;
                logger.LogError(
                    "Not enough stock to confirm order {ExternalReference} for product {ProductId} (quantity {Quantity}).",
                    order.ExternalReference,
                    item.ProductId,
                    item.Quantity);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        order.StockApplied = true;
        logger.LogInformation(
            "Stock discounted for order {ExternalReference}: {LineCount} product line(s).",
            order.ExternalReference,
            items.Count);
        return true;
    }
}
