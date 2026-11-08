namespace AppDistribuidaAspire.Web.Models;

public sealed record StoreProduct(
    int Id,
    string Name,
    string Category,
    string Brand,
    string Description,
    decimal Price,
    int Stock,
    string ImageUrl);
