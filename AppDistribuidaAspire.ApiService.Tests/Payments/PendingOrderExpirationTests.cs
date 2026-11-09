using AppDistribuidaAspire.ApiService;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AppDistribuidaAspire.ApiService.Tests.Payments;

public sealed class PendingOrderExpirationTests : IDisposable
{
    private readonly ThisContext _database;
    private readonly string _databasePath;

    public PendingOrderExpirationTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"pending-expiration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ThisContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;
        _database = new ThisContext(options);
        _database.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Best effort cleanup on Windows file locking.
        }
    }

    [Fact]
    public async Task PendingOrderOlderThanWindow_BecomesCancelled()
    {
        _database.PaymentOrders.Add(new PaymentOrder
        {
            ExternalReference = "expired",
            PreferenceId = "pref-1",
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow.Subtract(PendingOrderExpiration.Window).AddMilliseconds(-1),
            Amount = 100m
        });
        await _database.SaveChangesAsync();

        var cancelled = await PendingOrderExpiration.CancelExpiredAsync(_database);
        // ExecuteUpdate writes directly to the database without touching tracked entities.
        _database.ChangeTracker.Clear();

        var order = await _database.PaymentOrders.SingleAsync(candidate => candidate.ExternalReference == "expired");
        Assert.Equal(1, cancelled);
        Assert.Equal("cancelled", order.Status);
        Assert.Equal(PendingOrderExpiration.ExpiryStatusDetail, order.StatusDetail);
    }

    [Fact]
    public async Task PendingOrderInsideWindow_KeepsPending()
    {
        _database.PaymentOrders.Add(new PaymentOrder
        {
            ExternalReference = "fresh",
            PreferenceId = "pref-2",
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1)),
            Amount = 100m
        });
        await _database.SaveChangesAsync();

        var cancelled = await PendingOrderExpiration.CancelExpiredAsync(_database);

        var order = await _database.PaymentOrders.SingleAsync(candidate => candidate.ExternalReference == "fresh");
        Assert.Equal(0, cancelled);
        Assert.Equal("pending", order.Status);
    }

    [Fact]
    public async Task ApprovedOrder_NeverExpires()
    {
        _database.PaymentOrders.Add(new PaymentOrder
        {
            ExternalReference = "approved-old",
            PreferenceId = "demo",
            Status = "approved",
            CreatedAtUtc = DateTime.UtcNow.Subtract(TimeSpan.FromDays(2)),
            Amount = 100m
        });
        await _database.SaveChangesAsync();

        var cancelled = await PendingOrderExpiration.CancelExpiredAsync(_database);

        var order = await _database.PaymentOrders.SingleAsync(candidate => candidate.ExternalReference == "approved-old");
        Assert.Equal(0, cancelled);
        Assert.Equal("approved", order.Status);
    }

    [Fact]
    public async Task PendingOrderWithPaymentId_NeverExpires()
    {
        _database.PaymentOrders.Add(new PaymentOrder
        {
            ExternalReference = "awaiting-webhook",
            PreferenceId = "pref-3",
            PaymentId = "123456",
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow.Subtract(TimeSpan.FromHours(3)),
            Amount = 100m
        });
        await _database.SaveChangesAsync();

        var cancelled = await PendingOrderExpiration.CancelExpiredAsync(_database);

        var order = await _database.PaymentOrders.SingleAsync(candidate => candidate.ExternalReference == "awaiting-webhook");
        Assert.Equal(0, cancelled);
        Assert.Equal("pending", order.Status);
    }
}
