namespace AppDistribuidaAspire.ApiService.Payments;

public sealed record CreateCheckoutRequest(IReadOnlyList<CreateCheckoutItem>? Items);

public sealed record CreateCheckoutItem(int ProductId, int Quantity);

public sealed record CreateCheckoutResponse(string PreferenceId, string CheckoutUrl);

public sealed record CheckoutLine(CatalogProduct Product, int Quantity);

public sealed record VerifiedPaymentResponse(
    string PaymentId,
    string PreferenceId,
    string Status,
    string? StatusDetail,
    decimal Amount,
    string Currency,
    string? PaymentMethodId,
    DateTime? ApprovedAtUtc);
