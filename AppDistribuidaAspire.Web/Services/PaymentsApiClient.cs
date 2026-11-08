using System.Net.Http.Json;

namespace AppDistribuidaAspire.Web.Services;

public sealed class PaymentsApiClient(HttpClient httpClient)
{
    public async Task<CheckoutPreferenceResponse> CreateCheckoutAsync(
        int productId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/payments/checkout-preferences",
            new CreateCheckoutRequest(productId, 1),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("No se pudo iniciar el pago con Mercado Pago.");
        }

        return await response.Content.ReadFromJsonAsync<CheckoutPreferenceResponse>(cancellationToken)
            ?? throw new HttpRequestException("El API no devolvió una URL de pago válida.");
    }

    private sealed record CreateCheckoutRequest(int ProductId, int Quantity);
}

public sealed record CheckoutPreferenceResponse(string PreferenceId, string CheckoutUrl);
