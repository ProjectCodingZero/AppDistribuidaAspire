namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class PaymentOrder
{
    public int Id { get; set; }
    public required string ExternalReference { get; set; }
    public string PreferenceId { get; set; } = string.Empty;
    public string? CheckoutUrl { get; set; }
    public string? PaymentId { get; set; }
    public required string Status { get; set; }
    public string? StatusDetail { get; set; }
    public string? UserId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ARS";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public bool IsDevelopmentSample { get; set; }
    public bool StockApplied { get; set; }
    public List<PaymentOrderItem> Items { get; set; } = [];
}
