namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class MercadoPagoOptions
{
    public string? AccessToken { get; set; }
    public bool UseSandbox { get; set; } = true;
    public string? SuccessUrl { get; set; }
    public string? FailureUrl { get; set; }
    public string? PendingUrl { get; set; }
    public string? NotificationUrl { get; set; }
    public string? WebhookSecret { get; set; }
}
