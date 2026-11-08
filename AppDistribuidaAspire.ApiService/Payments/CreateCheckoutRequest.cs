namespace AppDistribuidaAspire.ApiService.Payments;

public sealed record CreateCheckoutRequest(int ProductId, int Quantity);

public sealed record CreateCheckoutResponse(string PreferenceId, string CheckoutUrl);

public sealed record CheckoutProduct(int Id, string Name, decimal Price);

public static class CheckoutCatalog
{
    private static readonly IReadOnlyDictionary<int, CheckoutProduct> Products = new Dictionary<int, CheckoutProduct>
    {
        [1] = new(1, "Auriculares inalámbricos", 84999m),
        [2] = new(2, "Reloj inteligente", 124999m),
        [3] = new(3, "Lámpara de mesa", 42999m),
        [4] = new(4, "Mochila urbana", 59999m),
        [5] = new(5, "Zapatillas deportivas", 72999m),
        [6] = new(6, "Cámara instantánea", 159999m)
    };

    public static bool TryGetProduct(int productId, out CheckoutProduct product) =>
        Products.TryGetValue(productId, out product!);
}
