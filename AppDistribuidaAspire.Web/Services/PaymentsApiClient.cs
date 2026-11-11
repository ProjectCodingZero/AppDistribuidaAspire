using System.Net.Http.Json;
using System.Net.Http.Headers;
using AppDistribuidaAspire.Web.Models;
using Microsoft.AspNetCore.Components.Authorization;

namespace AppDistribuidaAspire.Web.Services;

public sealed class PaymentsApiClient(HttpClient httpClient, AuthenticationStateProvider authenticationStateProvider)
{
    public async Task<IReadOnlyList<StoreProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/products", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<StoreProduct>>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió el catálogo de productos.");
    }

    public async Task<CheckoutConfig> GetCheckoutConfigAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("api/payments/checkout-config", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<CheckoutConfig>(cancellationToken)
            ?? new CheckoutConfig("MercadoPago", false, true, null);
    }

    public async Task<CheckoutPreferenceResponse> CreateCheckoutAsync(
        IReadOnlyCollection<CartCheckoutItem> items,
        string returnBaseUrl,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/payments/checkout-preferences",
            cancellationToken);
        request.Content = JsonContent.Create(new CreateCheckoutRequest(items, returnBaseUrl));
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ReadApiErrorMessageAsync(response, "No se pudo iniciar el pago con Mercado Pago."));
        }

        return await response.Content.ReadFromJsonAsync<CheckoutPreferenceResponse>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió una URL de pago válida.");
    }

    public async Task<VerifiedPayment?> VerifyPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/payments/verify/{Uri.EscapeDataString(paymentId)}",
            cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("No se pudo verificar el pago con Mercado Pago.");
        }

        return await response.Content.ReadFromJsonAsync<VerifiedPayment>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió los datos verificados del pago.");
    }

    public async Task<VerifiedPayment?> GetOrderStatusAsync(string externalReference, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/payments/return/{Uri.EscapeDataString(externalReference)}",
            cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("No se pudo consultar el estado de la orden.");
        }

        return await response.Content.ReadFromJsonAsync<VerifiedPayment>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió el estado de la orden.");
    }

    public async Task<IReadOnlyList<PurchaseHistoryEntry>> GetPurchaseHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(HttpMethod.Get, "api/account/purchases", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("No se pudo cargar el historial de compras de la cuenta.");
        }

        return await response.Content.ReadFromJsonAsync<List<PurchaseHistoryEntry>>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió el historial de compras.");
    }

    public async Task<CheckoutPreferenceResponse> RetryCheckoutAsync(
        string externalReference,
        string returnBaseUrl,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/payments/orders/{Uri.EscapeDataString(externalReference)}/retry-checkout",
            cancellationToken);
        request.Content = JsonContent.Create(new RetryCheckoutRequest(returnBaseUrl));
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ReadApiErrorMessageAsync(response, "No se pudo retomar el pago. Intentá otra vez."));
        }

        return await response.Content.ReadFromJsonAsync<CheckoutPreferenceResponse>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió una URL de pago válida.");
    }

    public async Task<PurchaseHistoryEntry?> GetPurchaseAsync(
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/account/purchases/{Uri.EscapeDataString(externalReference)}",
            cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("No se pudo cargar el detalle del pedido.");
        }

        return await response.Content.ReadFromJsonAsync<PurchaseHistoryEntry>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió el detalle del pedido.");
    }

    private static async Task<string> ReadApiErrorMessageAsync(HttpResponseMessage response, string fallback)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ApiErrorPayload>();
            if (!string.IsNullOrWhiteSpace(payload?.Error))
            {
                return payload.Error;
            }

            if (!string.IsNullOrWhiteSpace(payload?.Detail))
            {
                return payload.Detail;
            }

            if (!string.IsNullOrWhiteSpace(payload?.Title))
            {
                return payload.Title;
            }

            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload.Message;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Fall back to the generic message.
        }

        return fallback;
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        string uri,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var accessToken = authenticationState.User.FindFirst("nexo:access_token")?.Value;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private sealed record CreateCheckoutRequest(IReadOnlyCollection<CartCheckoutItem> Items, string ReturnBaseUrl);

    private sealed record RetryCheckoutRequest(string ReturnBaseUrl);

    private sealed record ApiErrorPayload(string? Error, string? Detail, string? Title, string? Message);
}

public sealed record CartCheckoutItem(int ProductId, int Quantity);

public sealed record CheckoutPreferenceResponse(string PreferenceId, string CheckoutUrl);

public sealed record CheckoutConfig(string Mode, bool AutoApprove, bool Ready, string? Warning)
{
    public bool IsDemo => string.Equals(Mode, "Demo", StringComparison.OrdinalIgnoreCase);
    public bool IsReady => IsDemo || Ready;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
}

public sealed record VerifiedPayment(
    string PaymentId,
    string PreferenceId,
    string Status,
    string? StatusDetail,
    decimal Amount,
    string Currency,
    string? PaymentMethodId,
    DateTime? ApprovedAtUtc);

public sealed record PurchaseHistoryEntry(
    int Id,
    string ExternalReference,
    string Status,
    string? StatusDetail,
    decimal Amount,
    string Currency,
    DateTime CreatedAtUtc,
    DateTime? ApprovedAtUtc,
    bool IsDevelopmentSample,
    IReadOnlyList<PurchaseHistoryLine> Items);

public sealed record PurchaseHistoryLine(
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);
