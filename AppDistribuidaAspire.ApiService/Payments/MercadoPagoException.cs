using System.Net;

namespace AppDistribuidaAspire.ApiService.Payments;

/// <summary>
/// Raised when Mercado Pago rejects a request. It keeps the provider status and
/// code so the API can return an actionable message instead of a generic 502.
/// </summary>
public sealed class MercadoPagoException : Exception
{
    public MercadoPagoException(
        string message,
        HttpStatusCode? statusCode = null,
        string? providerCode = null,
        string? providerMessage = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ProviderCode = providerCode;
        ProviderMessage = providerMessage;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ProviderCode { get; }
    public string? ProviderMessage { get; }
}
