using System.Text.Json;
using AppDistribuidaAspire.Web.Models;
using Microsoft.JSInterop;

namespace AppDistribuidaAspire.Web.Services;

public sealed class CartService(IJSRuntime jsRuntime)
{
    private readonly Dictionary<int, CartLine> _lines = [];
    private bool _isInitialized;

    public event Action? Changed;

    public bool IsInitialized => _isInitialized;
    public IReadOnlyCollection<CartLine> Items => _lines.Values.OrderBy(line => line.Product.Name).ToArray();
    public int ItemCount => _lines.Values.Sum(line => line.Quantity);
    public decimal Total => _lines.Values.Sum(line => line.Product.Price * line.Quantity);

    public async Task InitializeAsync(IReadOnlyCollection<StoreProduct> catalog)
    {
        if (_isInitialized)
        {
            return;
        }

        try
        {
            var storedJson = await jsRuntime.InvokeAsync<string?>("cartStorage.get");
            if (!string.IsNullOrWhiteSpace(storedJson))
            {
                var savedLines = JsonSerializer.Deserialize<List<SavedCartLine>>(storedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var productsById = catalog.ToDictionary(product => product.Id);

                foreach (var savedLine in savedLines ?? [])
                {
                    if (savedLine.Quantity <= 0 || !productsById.TryGetValue(savedLine.ProductId, out var product) || product.Stock <= 0)
                    {
                        continue;
                    }

                    var quantity = Math.Min(savedLine.Quantity, product.Stock);
                    if (_lines.TryGetValue(product.Id, out var existing))
                    {
                        quantity = Math.Min(existing.Quantity + quantity, product.Stock);
                    }

                    _lines[product.Id] = new CartLine(product, quantity);
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (JSException)
        {
        }

        _isInitialized = true;
        Changed?.Invoke();
    }

    public async Task AddAsync(StoreProduct product)
    {
        if (!_isInitialized || product.Stock <= 0)
        {
            return;
        }

        if (_lines.TryGetValue(product.Id, out var current))
        {
            if (current.Quantity >= product.Stock)
            {
                return;
            }

            _lines[product.Id] = current with { Quantity = current.Quantity + 1 };
        }
        else
        {
            _lines[product.Id] = new CartLine(product, 1);
        }

        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task SetQuantityAsync(int productId, int quantity)
    {
        if (!_isInitialized || !_lines.TryGetValue(productId, out var current))
        {
            return;
        }

        if (quantity <= 0)
        {
            _lines.Remove(productId);
        }
        else
        {
            _lines[productId] = current with { Quantity = Math.Min(quantity, current.Product.Stock) };
        }

        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task RemoveAsync(int productId)
    {
        if (_isInitialized && _lines.Remove(productId))
        {
            await PersistAsync();
            Changed?.Invoke();
        }
    }

    public async Task ClearAsync()
    {
        if (_isInitialized && _lines.Count > 0)
        {
            _lines.Clear();
            await PersistAsync();
            Changed?.Invoke();
        }
    }

    private async Task PersistAsync()
    {
        var savedLines = _lines.Values
            .Select(line => new SavedCartLine(line.Product.Id, line.Quantity))
            .ToArray();

        try
        {
            await jsRuntime.InvokeVoidAsync("cartStorage.set", JsonSerializer.Serialize(savedLines));
        }
        catch (JSException)
        {
        }
    }

    private sealed record SavedCartLine(int ProductId, int Quantity);
}

public sealed record CartLine(StoreProduct Product, int Quantity);
