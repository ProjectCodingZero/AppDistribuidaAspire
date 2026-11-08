using System.Reflection;
using System.Text.Json;

namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class ProductCatalog
{
    private const string ResourceName = "AppDistribuidaAspire.ApiService.Data.products.json";
    private readonly IReadOnlyDictionary<int, CatalogProduct> _products;

    public ProductCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded product catalog '{ResourceName}' was not found.");

        var products = JsonSerializer.Deserialize<List<CatalogProduct>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The product catalog JSON is empty or invalid.");

        if (products.Count == 0 || products.Any(product => product.Id <= 0 || product.Price <= 0 || product.Stock < 0) ||
            products.Select(product => product.Id).Distinct().Count() != products.Count)
        {
            throw new InvalidOperationException("The product catalog contains invalid or duplicate products.");
        }

        _products = products.ToDictionary(product => product.Id);
        Products = products.AsReadOnly();
    }

    public IReadOnlyList<CatalogProduct> Products { get; }

    public bool TryGetProduct(int productId, out CatalogProduct product) =>
        _products.TryGetValue(productId, out product!);
}
