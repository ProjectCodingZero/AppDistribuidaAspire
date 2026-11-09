namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class MercadoPagoOptions
{
    public string? AccessToken { get; set; }
    public bool UseSandbox { get; set; } = true;

    /// <summary>
    /// "MercadoPago" redirects to Mercado Pago Checkout Pro. "Demo" approves the
    /// purchase locally so the store always works without Mercado Pago accounts.
    /// </summary>
    public string CheckoutMode { get; set; } = "MercadoPago";

    /// <summary>
    /// When true, a checkout created through Mercado Pago is recorded as approved
    /// immediately. Useful for demos where the sandbox payment is not completed.
    /// </summary>
    public bool AutoApproveCheckout { get; set; }
    public string? SuccessUrl { get; set; }
    public string? FailureUrl { get; set; }
    public string? PendingUrl { get; set; }
    public string? NotificationUrl { get; set; }
    public string? WebhookSecret { get; set; }
}
