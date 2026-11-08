namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class PaymentOrderItem
{
    public int Id { get; set; }
    public int PaymentOrderId { get; set; }
    public int ProductId { get; set; }
    public required string ProductName { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}