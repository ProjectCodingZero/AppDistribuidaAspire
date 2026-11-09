using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class MercadoPagoCheckoutService(
    IHttpClientFactory httpClientFactory,
    IOptions<MercadoPagoOptions> options,
    IHostEnvironment environment)
{
    private readonly MercadoPagoOptions _options = options.Value;

    public async Task<CreateCheckoutResponse> CreatePreferenceAsync(
        IReadOnlyList<CheckoutLine> items,
        string externalReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new InvalidOperationException("Mercado Pago AccessToken is not configured.");
        }

        // Mercado Pago now issues test credentials with the APP_USR- prefix too (the test seller
        // account is created automatically), so the prefix alone no longer tells test from
        // production. The only combination we can safely reject is a TEST- token in production,
        // because it can never process real payments.
        if (!_options.UseSandbox && _options.AccessToken.StartsWith("TEST-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Mercado Pago production requires a production access token (APP_USR-). TEST- tokens only work in sandbox mode.");
        }

        if (!Uri.TryCreate(_options.SuccessUrl, UriKind.Absolute, out var successUrl) ||
            !Uri.TryCreate(_options.FailureUrl, UriKind.Absolute, out var failureUrl) ||
            !Uri.TryCreate(_options.PendingUrl, UriKind.Absolute, out var pendingUrl) ||
            !IsAllowedReturnUrl(successUrl) ||
            !IsAllowedReturnUrl(failureUrl) ||
            !IsAllowedReturnUrl(pendingUrl) ||
            (!string.IsNullOrWhiteSpace(_options.NotificationUrl) &&
             (!Uri.TryCreate(_options.NotificationUrl, UriKind.Absolute, out var notificationUrl) ||
              !IsAllowedNotificationUrl(notificationUrl))))
        {
            throw new InvalidOperationException(
                "Mercado Pago return URLs must use HTTPS. The notification URL must use a public HTTPS host reachable by Mercado Pago.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "checkout/preferences");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        request.Content = JsonContent.Create(new MercadoPagoPreferenceRequest
        {
            Items = items.Select(line => new MercadoPagoPreferenceItem
            {
                Id = line.Product.Id.ToString(),
                Title = line.Product.Name,
                Quantity = line.Quantity,
                CurrencyId = "ARS",
                UnitPrice = line.Product.Price
            }).ToArray(),
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
            ExternalReference = externalReference
        });

        var client = httpClientFactory.CreateClient(nameof(MercadoPagoCheckoutService));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var preference = await response.Content.ReadFromJsonAsync<MercadoPagoPreferenceResponse>(cancellationToken);

        // With test credentials Mercado Pago already routes init_point to the sandbox.
        // sandbox_init_point is deprecated and can make the checkout fail with
        // "Una de las partes con la que intentás hacer el pago es de prueba".
        var checkoutUrl = preference?.InitPoint;

        if (string.IsNullOrWhiteSpace(preference?.Id) || string.IsNullOrWhiteSpace(checkoutUrl))
        {
            throw new HttpRequestException("Mercado Pago returned an incomplete checkout preference.");
        }

        return new CreateCheckoutResponse(preference.Id, checkoutUrl);
    }

    private bool IsAllowedReturnUrl(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (environment.IsDevelopment())
        {
            return true;
        }

        var host = uri.Host;
        return !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
               !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
               (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address));
    }

    private static bool IsAllowedNotificationUrl(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address);
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
    }
}
