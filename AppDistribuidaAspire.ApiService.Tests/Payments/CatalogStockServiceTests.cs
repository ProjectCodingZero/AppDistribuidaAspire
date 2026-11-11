using AppDistribuidaAspire.ApiService;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AppDistribuidaAspire.ApiService.Tests.Payments;

public sealed class CatalogStockServiceTests : IDisposable
{
    private readonly ThisContext _database;
    private readonly string _databasePath;
    private readonly ProductCatalog _catalog = new();

    public CatalogStockServiceTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"catalog-stock-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ThisContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _database = new ThisContext(options);
        _database.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Best effort cleanup on Windows file locking.
        }
    }

    [Fact]
    public async Task EnsureInitialized_UsesCatalogInitialStock()
    {
        var service = CreateService();

        await service.EnsureInitializedAsync();

        var products = await service.GetProductsAsync();
        Assert.Equal(_catalog.Products.Count, products.Count);
        Assert.All(products, product =>
        {
            var catalogProduct = _catalog.Products.Single(candidate => candidate.Id == product.Id);
            Assert.Equal(catalogProduct.Stock, product.Stock);
            Assert.True(product.Stock > 100, "The recreated catalog must have a large stock.");
        });
    }

    [Fact]
    public async Task TryApplyStock_DiscountsExactlyOnce()
    {
        var service = CreateService();
        await service.EnsureInitializedAsync();
        var product = _catalog.Products[0];
        var order = CreateOrder("stock-once", product, quantity: 3);
        _database.PaymentOrders.Add(order);
        await _database.SaveChangesAsync();

        Assert.True(await service.TryApplyStockAsync(order));

        _database.ChangeTracker.Clear();
        var reloadedOrder = await _database.PaymentOrders
            .Include(candidate => candidate.Items)
            .SingleAsync(candidate => candidate.ExternalReference == "stock-once");
        Assert.True(await service.TryApplyStockAsync(reloadedOrder));

        _database.ChangeTracker.Clear();
        var stock = await _database.ProductStocks.SingleAsync(candidate => candidate.ProductId == product.Id);
        Assert.Equal(product.Stock - 3, stock.Available);
    }

    [Fact]
    public async Task TryApplyStock_RejectsPurchaseWhenStockIsNotEnough()
    {
        var service = CreateService();
        await service.EnsureInitializedAsync();
        var product = _catalog.Products[1];

        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "ProductStocks" SET "Available" = 1 WHERE "ProductId" = {product.Id}""");

        var order = CreateOrder("stock-shortage", product, quantity: 2);
        _database.PaymentOrders.Add(order);
        await _database.SaveChangesAsync();

        Assert.False(await service.TryApplyStockAsync(order));

        _database.ChangeTracker.Clear();
        var stock = await _database.ProductStocks.SingleAsync(candidate => candidate.ProductId == product.Id);
        var persistedOrder = await _database.PaymentOrders.SingleAsync(candidate => candidate.ExternalReference == "stock-shortage");
        Assert.Equal(1, stock.Available);
        Assert.False(persistedOrder.StockApplied);
    }

    private CatalogStockService CreateService() =>
        new(_catalog, _database, NullLogger<CatalogStockService>.Instance);

    private static PaymentOrder CreateOrder(string externalReference, CatalogProduct product, int quantity) =>
        new()
        {
            ExternalReference = externalReference,
            PreferenceId = "demo",
            Status = "approved",
            Amount = product.Price * quantity,
            Items =
            [
                new PaymentOrderItem
                {
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Quantity = quantity,
                    UnitPrice = product.Price
                }
            ]
        };
}
