using System.Security.Claims;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").RequireAuthorization();
        group.MapGet("/purchases", GetPurchasesAsync);
        group.MapGet("/purchases/{externalReference}", GetPurchaseAsync);

        return app;
    }

    private static async Task<IResult> GetPurchasesAsync(
        ClaimsPrincipal principal,
        ThisContext database,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        await PendingOrderExpiration.CancelExpiredAsync(database, cancellationToken);

        var purchases = await database.PaymentOrders
            .AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.UserId == userId)
            .OrderByDescending(order => order.CreatedAtUtc)
            .Select(order => new
            {
                order.Id,
                order.ExternalReference,
                order.Status,
                order.StatusDetail,
                order.Amount,
                order.Currency,
                order.CreatedAtUtc,
                order.ApprovedAtUtc,
                order.IsDevelopmentSample,
                Items = order.Items.Select(item => new
                {
                    item.ProductId,
                    item.ProductName,
                    item.Quantity,
                    item.UnitPrice
                }).ToArray()
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(purchases);
    }

    private static async Task<IResult> GetPurchaseAsync(
        string externalReference,
        ClaimsPrincipal principal,
        ThisContext database,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Results.Unauthorized();
        }

        await PendingOrderExpiration.CancelExpiredAsync(database, cancellationToken);

        var purchase = await database.PaymentOrders
            .AsNoTracking()
            .Include(order => order.Items)
            .Where(order => order.UserId == userId && order.ExternalReference == externalReference)
            .OrderByDescending(order => order.CreatedAtUtc)
            .Select(order => new
            {
                order.Id,
                order.ExternalReference,
                order.Status,
                order.StatusDetail,
                order.Amount,
                order.Currency,
                order.CreatedAtUtc,
                order.ApprovedAtUtc,
                order.IsDevelopmentSample,
                Items = order.Items.Select(item => new
                {
                    item.ProductId,
                    item.ProductName,
                    item.Quantity,
                    item.UnitPrice
                }).ToArray()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return purchase is null ? Results.NotFound() : Results.Ok(purchase);
    }
}
