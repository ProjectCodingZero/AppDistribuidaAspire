using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class MercadoPagoCheckoutService(
    IHttpClientFactory httpClientFactory,
    IOptions<MercadoPagoOptions> options)
{
    private readonly MercadoPagoOptions _options = options.Value;

    public async Task<CreateCheckoutResponse> CreatePreferenceAsync(
        CheckoutProduct product,
        int quantity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new InvalidOperationException("Mercado Pago AccessToken is not configured.");
        }

        if (!Uri.TryCreate(_options.SuccessUrl, UriKind.Absolute, out var successUrl) ||
            !Uri.TryCreate(_options.FailureUrl, UriKind.Absolute, out var failureUrl) ||
            !Uri.TryCreate(_options.PendingUrl, UriKind.Absolute, out var pendingUrl))
        {
            throw new InvalidOperationException("Mercado Pago return URLs are not configured with absolute URLs.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "checkout/preferences");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        request.Content = JsonContent.Create(new MercadoPagoPreferenceRequest
        {
            Items = [new MercadoPagoPreferenceItem
            {
                Id = product.Id.ToString(),
                Title = product.Name,
                Quantity = quantity,
                CurrencyId = "ARS",
                UnitPrice = product.Price
            }],
            BackUrls = new MercadoPagoBackUrls
            {
                Success = successUrl.ToString(),
                Failure = failureUrl.ToString(),
                Pending = pendingUrl.ToString()
            },
            AutoReturn = "approved",
            NotificationUrl = string.IsNullOrWhiteSpace(_options.NotificationUrl)
                ? null
                : _options.NotificationUrl,
            ExternalReference = Guid.NewGuid().ToString("N")
        });

        var client = httpClientFactory.CreateClient(nameof(MercadoPagoCheckoutService));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var preference = await response.Content.ReadFromJsonAsync<MercadoPagoPreferenceResponse>(cancellationToken);
        var checkoutUrl = _options.UseSandbox ? preference?.SandboxInitPoint : preference?.InitPoint;

        if (string.IsNullOrWhiteSpace(preference?.Id) || string.IsNullOrWhiteSpace(checkoutUrl))
        {
            throw new HttpRequestException("Mercado Pago returned an incomplete checkout preference.");
        }

        return new CreateCheckoutResponse(preference.Id, checkoutUrl);
    }

    private sealed class MercadoPagoPreferenceRequest
    {
        [JsonPropertyName("items")]
        public required IReadOnlyList<MercadoPagoPreferenceItem> Items { get; init; }

        [JsonPropertyName("back_urls")]
        public required MercadoPagoBackUrls BackUrls { get; init; }

        [JsonPropertyName("auto_return")]
        public string? AutoReturn { get; init; }

        [JsonPropertyName("notification_url")]
        public string? NotificationUrl { get; init; }

        [JsonPropertyName("external_reference")]
        public required string ExternalReference { get; init; }
    }

    private sealed class MercadoPagoPreferenceItem
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; init; }

        [JsonPropertyName("currency_id")]
        public required string CurrencyId { get; init; }

        [JsonPropertyName("unit_price")]
        public decimal UnitPrice { get; init; }
    }

    private sealed class MercadoPagoBackUrls
    {
        [JsonPropertyName("success")]
        public required string Success { get; init; }

        [JsonPropertyName("failure")]
        public required string Failure { get; init; }

        [JsonPropertyName("pending")]
        public required string Pending { get; init; }
    }

    private sealed class MercadoPagoPreferenceResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("init_point")]
        public string? InitPoint { get; init; }

        [JsonPropertyName("sandbox_init_point")]
        public string? SandboxInitPoint { get; init; }
    }
}
