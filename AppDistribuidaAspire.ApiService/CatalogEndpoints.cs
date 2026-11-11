using AppDistribuidaAspire.ApiService.Payments;

namespace AppDistribuidaAspire.ApiService;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/products", async (
            CatalogStockService stockService,
            CancellationToken cancellationToken) =>
        {
            var products = await stockService.GetProductsAsync(cancellationToken);
            return Results.Ok(products);
        });

        return app;
    }
}
