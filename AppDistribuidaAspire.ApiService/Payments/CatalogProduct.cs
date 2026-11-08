namespace AppDistribuidaAspire.ApiService.Payments;

public sealed record CatalogProduct(
    int Id,
    string Name,
    string Category,
    string Brand,
    string Description,
    decimal Price,
    int Stock,
    string ImageUrl);
