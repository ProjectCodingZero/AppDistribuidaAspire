using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class MercadoPagoCheckoutService(
    IHttpClientFactory httpClientFactory,
    IOptions<MercadoPagoOptions> options,
    IHostEnvironment environment,
    ILogger<MercadoPagoCheckoutService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MercadoPagoOptions _options = options.Value;

    /// <summary>
    /// Returns a human readable warning for the current configuration, or null when
    /// everything looks correct. A legacy TEST- token produces a warning but is still
    /// allowed to be used (see <see cref="CanAttemptCheckout"/>).
    /// </summary>
    public string? GetConfigurationWarning()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            return "Falta configurar MercadoPago:AccessToken. Pegá un AccessToken de prueba (APP_USR-...) en appsettings.Development.json.";
        }

        // Mercado Pago discontinued the legacy TEST- credentials. We still allow
        // trying them, but warn that the provider will probably reject them.
        if (IsLegacyTestToken())
        {
            return "Estás usando un AccessToken TEST-..., discontinuado por Mercado Pago. El checkout se intentará igual, pero es probable que Mercado Pago lo rechace. Si falla, generá un AccessToken de prueba APP_USR-... en el panel de desarrolladores.";
        }

        if (!_options.AccessToken.StartsWith("APP_USR-", StringComparison.OrdinalIgnoreCase))
        {
            return "El AccessToken no tiene el formato esperado (APP_USR-...). Revisá las credenciales de Mercado Pago.";
        }

        if (!string.IsNullOrWhiteSpace(_options.NotificationUrl) &&
            (!Uri.TryCreate(_options.NotificationUrl, UriKind.Absolute, out var notificationUrl) ||
             !IsAllowedNotificationUrl(notificationUrl)))
        {
            return "MercadoPago:NotificationUrl debe ser una URL HTTPS pública y accesible por Mercado Pago.";
        }

        return null;
    }

    /// <summary>
    /// True when the checkout can be attempted. Legacy TEST- tokens are allowed on
    /// purpose: they are surfaced as a warning instead of blocking the purchase flow.
    /// </summary>
    public bool CanAttemptCheckout()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            return false;
        }

        if (IsLegacyTestToken())
        {
            return true;
        }

        if (!_options.AccessToken.StartsWith("APP_USR-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_options.NotificationUrl) &&
            (!Uri.TryCreate(_options.NotificationUrl, UriKind.Absolute, out var notificationUrl) ||
             !IsAllowedNotificationUrl(notificationUrl)))
        {
            return false;
        }

        return true;
    }

    private bool IsLegacyTestToken() =>
        _options.AccessToken?.StartsWith("TEST-", StringComparison.Ordinal) == true;

    public async Task<CreateCheckoutResponse> CreatePreferenceAsync(
        IReadOnlyList<CheckoutLine> items,
        string externalReference,
        string? returnBaseUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new InvalidOperationException(
                "Mercado Pago no está configurado. Agregá MercadoPago:AccessToken en appsettings.Development.json.");
        }

        if (IsLegacyTestToken())
        {
            logger.LogWarning(
                "Using a legacy Mercado Pago TEST- access token. The checkout will be attempted, but Mercado Pago may reject it; generate an APP_USR- test token in the developer panel.");
        }

        if (!TryBuildReturnUrls(returnBaseUrl, out var returnUrls, out var returnUrlsError))
        {
            throw new InvalidOperationException(returnUrlsError);
        }

        if (!string.IsNullOrWhiteSpace(_options.NotificationUrl) &&
            (!Uri.TryCreate(_options.NotificationUrl, UriKind.Absolute, out var notificationUrl) ||
             !IsAllowedNotificationUrl(notificationUrl)))
        {
            throw new InvalidOperationException(
                "MercadoPago:NotificationUrl debe ser una URL HTTPS pública y accesible por Mercado Pago.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "checkout/preferences");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        request.Content = JsonContent.Create(new MercadoPagoPreferenceRequest
        {
            Items = items.Select(line => new MercadoPagoPreferenceItem
            {
                Id = line.Product.Id.ToString(),
                Title = line.Product.Name,
                Description = line.Product.Description,
                Quantity = line.Quantity,
                CurrencyId = "ARS",
                UnitPrice = line.Product.Price
            }).ToArray(),
            BackUrls = new MercadoPagoBackUrls
            {
                Success = returnUrls.Success,
                Failure = returnUrls.Failure,
                Pending = returnUrls.Pending
            },
            // auto_return only works with publicly reachable HTTPS URLs. During
            // local development we let the buyer use the "Volver al sitio" button.
            AutoReturn = returnUrls.IsPublicSuccess ? "approved" : null,
            NotificationUrl = string.IsNullOrWhiteSpace(_options.NotificationUrl)
                ? null
                : _options.NotificationUrl,
            ExternalReference = externalReference
        }, options: JsonOptions);

        var client = httpClientFactory.CreateClient(nameof(MercadoPagoCheckoutService));
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var providerError = MercadoPagoErrorPayload.Parse(responseBody);
            logger.LogWarning(
                "Mercado Pago rejected preference {ExternalReference} with {StatusCode} ({ProviderCode}): {ProviderMessage}",
                externalReference,
                (int)response.StatusCode,
                providerError?.Code ?? providerError?.Error,
                providerError?.Message);

            throw BuildProviderException(response.StatusCode, providerError);
        }

        var preference = JsonSerializer.Deserialize<MercadoPagoPreferenceResponse>(responseBody, JsonOptions);

        // init_point is the recommended URL. sandbox_init_point is kept only as a
        // fallback for older accounts that still return it.
        var checkoutUrl = preference?.InitPoint;
        if (string.IsNullOrWhiteSpace(checkoutUrl))
        {
            checkoutUrl = preference?.SandboxInitPoint;
        }

        if (string.IsNullOrWhiteSpace(preference?.Id) || string.IsNullOrWhiteSpace(checkoutUrl))
        {
            throw new MercadoPagoException(
                "Mercado Pago devolvió una preferencia de pago incompleta. Intentá nuevamente en unos minutos.");
        }

        return new CreateCheckoutResponse(preference.Id, checkoutUrl);
    }

    private bool TryBuildReturnUrls(
        string? returnBaseUrl,
        out MercadoPagoReturnUrls returnUrls,
        out string? error)
    {
        returnUrls = default!;
        error = null;

        var baseUrl = returnBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(_options.SuccessUrl, UriKind.Absolute, out var configuredSuccess))
            {
                error = "MercadoPago:SuccessUrl debe ser una URL absoluta HTTPS.";
                return false;
            }

            baseUrl = configuredSuccess.GetLeftPart(UriPartial.Authority);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !IsAllowedReturnUrl(baseUri))
        {
            error = "La URL de retorno de la tienda debe ser HTTPS. Iniciá la app con el perfil HTTPS (por ejemplo, el AppHost de Aspire).";
            return false;
        }

        var origin = baseUri.GetLeftPart(UriPartial.Authority);
        returnUrls = new MercadoPagoReturnUrls(
            $"{origin}/payment/success",
            $"{origin}/payment/failure",
            $"{origin}/payment/pending",
            IsPublicHttps(baseUri));
        return true;
    }

    private static MercadoPagoException BuildProviderException(
        HttpStatusCode statusCode,
        MercadoPagoErrorPayload? providerError)
    {
        var providerCode = providerError?.Code ?? providerError?.Error;
        var providerMessage = providerError?.Message;
        var cause = providerError?.Cause?.FirstOrDefault()?.Description;

        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                "Mercado Pago rechazó las credenciales (401). Revisá que el AccessToken sea válido y corresponda al entorno de prueba.",
            HttpStatusCode.Forbidden =>
                "Mercado Pago bloqueó el acceso con este AccessToken (403). La cuenta o la credencial pueden estar revocadas; generá un AccessToken nuevo en el panel de desarrolladores.",
            HttpStatusCode.BadRequest =>
                $"Mercado Pago rechazó la preferencia de pago: {cause ?? providerMessage ?? "revisá los datos del carrito"}.",
            HttpStatusCode.NotFound =>
                "Mercado Pago no encontró el recurso solicitado. Verificá la configuración de la cuenta.",
            HttpStatusCode.TooManyRequests =>
                "Mercado Pago recibió demasiadas solicitudes. Esperá unos segundos y volvé a intentar.",
            _ when (int)statusCode >= 500 =>
                "Mercado Pago no está disponible en este momento. Intentá de nuevo en unos minutos.",
            _ =>
                $"Mercado Pago respondió con un error ({(int)statusCode}){(providerCode is null ? string.Empty : $" {providerCode}")}."
        };

        return new MercadoPagoException(message, statusCode, providerCode, providerMessage);
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

    private static bool IsPublicHttps(Uri uri)
    {
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

    private sealed record MercadoPagoReturnUrls(
        string Success,
        string Failure,
        string Pending,
        bool IsPublicSuccess);

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

        [JsonPropertyName("description")]
        public string? Description { get; init; }

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

    private sealed class MercadoPagoErrorPayload
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("status")]
        public int? Status { get; init; }

        [JsonPropertyName("cause")]
        public IReadOnlyList<MercadoPagoErrorCause>? Cause { get; init; }

        public static MercadoPagoErrorPayload? Parse(string? responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<MercadoPagoErrorPayload>(responseBody, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private sealed class MercadoPagoErrorCause
    {
        [JsonPropertyName("code")]
        public string? Code { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
