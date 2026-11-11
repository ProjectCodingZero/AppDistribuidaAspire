using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AppDistribuidaAspire.ApiService.Payments;

namespace AppDistribuidaAspire.ApiService;

public class ThisContext : IdentityDbContext<IdentityUser>
{
    public ThisContext(DbContextOptions<ThisContext> options)
        : base(options)
    {
    }

    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<ProductStock> ProductStocks => Set<ProductStock>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<PaymentOrder>()
            .HasIndex(order => order.ExternalReference)
            .IsUnique();

        builder.Entity<ProductStock>()
            .ToTable("ProductStocks")
            .HasKey(stock => stock.ProductId);

        builder.Entity<PaymentOrder>()
            .HasIndex(order => order.UserId);

        builder.Entity<PaymentOrder>()
            .HasMany(order => order.Items)
            .WithOne()
            .HasForeignKey(item => item.PaymentOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PaymentOrderItem>()
            .ToTable("PaymentOrderItems");

        builder.Entity<PaymentOrderItem>()
            .Property(item => item.UnitPrice)
            .HasPrecision(18, 2);
    }
}
