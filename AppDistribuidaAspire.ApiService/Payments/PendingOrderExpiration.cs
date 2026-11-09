using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService.Payments;

/// <summary>
/// Business rule: an order left without payment for more than
/// <see cref="Window"/> is cancelled automatically.
/// </summary>
public static class PendingOrderExpiration
{
    /// <summary>How long a pending order stays payable before being cancelled.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public const string ExpiryStatusDetail = "Cancelado: se venció el tiempo para pagar (5 minutos)";

    private static readonly string[] ExpirableStatuses = ["pending", "in_process", "creating_preference"];

    public static async Task<int> CancelExpiredAsync(ThisContext database, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.Subtract(Window);

        return await database.PaymentOrders
            .Where(order =>
                string.IsNullOrEmpty(order.PaymentId) &&
                order.CreatedAtUtc < cutoff &&
                ExpirableStatuses.Contains(order.Status))
            .ExecuteUpdateAsync(updates => updates
                    .SetProperty(order => order.Status, "cancelled")
                    .SetProperty(order => order.StatusDetail, ExpiryStatusDetail),
                cancellationToken);
    }

    public static bool IsPayable(string? status) => status is "pending" or "in_process";
}
