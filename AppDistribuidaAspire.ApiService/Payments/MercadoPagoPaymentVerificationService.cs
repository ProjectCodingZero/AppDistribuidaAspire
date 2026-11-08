using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AppDistribuidaAspire.ApiService.Payments;

public sealed class MercadoPagoPaymentVerificationService(
    IHttpClientFactory httpClientFactory,
    IOptions<MercadoPagoOptions> options,
    ThisContext database)
{
    private readonly MercadoPagoOptions _options = options.Value;

    public async Task<VerifiedPaymentResponse?> VerifyAsync(
        string paymentId,
        string userId,
        CancellationToken cancellationToken)
    {
        var payment = await GetPaymentAsync(paymentId, cancellationToken);
        if (payment is null || string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        return await ApplyPaymentAsync(payment, userId, cancellationToken);
    }

    public async Task<bool> ProcessWebhookAsync(string paymentId, CancellationToken cancellationToken)
    {
        var payment = await GetPaymentAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return false;
        }

        return await ApplyPaymentAsync(payment, userId: null, cancellationToken) is not null;
    }

    public async Task<VerifiedPaymentResponse?> GetOrderStatusAsync(
        string externalReference,
        string userId,
        CancellationToken cancellationToken)
    {
        var order = await database.PaymentOrders.SingleOrDefaultAsync(
            candidate => candidate.ExternalReference == externalReference && candidate.UserId == userId,
            cancellationToken);
        if (order is null || string.IsNullOrWhiteSpace(order.PreferenceId))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(order.PaymentId))
        {
            return await VerifyAsync(order.PaymentId, userId, cancellationToken);
        }

        return new VerifiedPaymentResponse(
            string.Empty,
            order.PreferenceId,
            order.Status,
            order.StatusDetail,
            order.Amount,
            order.Currency,
            null,
            order.ApprovedAtUtc);
    }

    private async Task<MercadoPagoPayment?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(paymentId, NumberStyles.None, CultureInfo.InvariantCulture, out var numericPaymentId) || numericPaymentId <= 0)
        {
            throw new ArgumentException("The payment ID must be a positive number.", nameof(paymentId));
        }

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
        {
            throw new InvalidOperationException("Mercado Pago AccessToken is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/payments/{numericPaymentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);

        var client = httpClientFactory.CreateClient(nameof(MercadoPagoCheckoutService));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payment = await response.Content.ReadFromJsonAsync<MercadoPagoPayment>(cancellationToken);
        return payment?.Id == numericPaymentId ? payment : null;
    }

    private async Task<VerifiedPaymentResponse?> ApplyPaymentAsync(
        MercadoPagoPayment payment,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payment.ExternalReference) ||
            string.IsNullOrWhiteSpace(payment.Status) ||
            payment.TransactionAmount is null ||
            string.IsNullOrWhiteSpace(payment.CurrencyId))
        {
            return null;
        }

        var order = await database.PaymentOrders.SingleOrDefaultAsync(
            candidate => candidate.ExternalReference == payment.ExternalReference &&
                         (userId == null || candidate.UserId == userId),
            cancellationToken);

        if (order is null ||
            string.IsNullOrWhiteSpace(order.PreferenceId) ||
            (!string.IsNullOrWhiteSpace(order.PaymentId) &&
             !string.Equals(order.PaymentId, payment.Id!.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)) ||
            order.Amount != payment.TransactionAmount.Value ||
            !string.Equals(order.Currency, payment.CurrencyId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A confirmed approval is immutable here: later/stale notifications must not downgrade it.
        if (!string.Equals(order.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            order.PaymentId = payment.Id!.Value.ToString(CultureInfo.InvariantCulture);
            order.Status = payment.Status;
            order.StatusDetail = payment.StatusDetail;
            order.ApprovedAtUtc = string.Equals(payment.Status, "approved", StringComparison.OrdinalIgnoreCase)
                ? payment.DateApproved?.UtcDateTime
                : null;
            await database.SaveChangesAsync(cancellationToken);
        }

        return new VerifiedPaymentResponse(
            order.PaymentId ?? payment.Id!.Value.ToString(CultureInfo.InvariantCulture),
            order.PreferenceId,
            order.Status,
            order.StatusDetail,
            order.Amount,
            order.Currency,
            payment.PaymentMethodId,
            order.ApprovedAtUtc);
    }

    private sealed class MercadoPagoPayment
    {
        [JsonPropertyName("id")]
        public long? Id { get; init; }

        [JsonPropertyName("external_reference")]
        public string? ExternalReference { get; init; }

        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("status_detail")]
        public string? StatusDetail { get; init; }

        [JsonPropertyName("transaction_amount")]
        public decimal? TransactionAmount { get; init; }

        [JsonPropertyName("currency_id")]
        public string? CurrencyId { get; init; }

        [JsonPropertyName("payment_method_id")]
        public string? PaymentMethodId { get; init; }

        [JsonPropertyName("date_approved")]
        public DateTimeOffset? DateApproved { get; init; }
    }
}
