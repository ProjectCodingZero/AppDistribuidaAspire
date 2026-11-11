namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class ProductStock
{
    public int ProductId { get; set; }
    public int Available { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
