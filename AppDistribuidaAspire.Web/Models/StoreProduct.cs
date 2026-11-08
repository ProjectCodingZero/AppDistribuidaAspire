namespace AppDistribuidaAspire.Web.Models;

public sealed record StoreProduct(
    int Id,
    string Name,
    string Category,
    decimal Price,
    string ImageUrl);
