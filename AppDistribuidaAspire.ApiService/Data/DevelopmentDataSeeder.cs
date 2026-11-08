using System.Security.Cryptography;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService.Data;

public static class DevelopmentDataSeeder
{
    private static readonly string[] DemoEmails =
    [
        "buyer.one@example.test",
        "buyer.two@example.test"
    ];

    public static async Task SeedAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var userManager = services.GetRequiredService<UserManager<IdentityUser>>();
        var database = services.GetRequiredService<ThisContext>();
        var catalog = services.GetRequiredService<ProductCatalog>();
        var password = configuration["DevelopmentSeed:Password"];
        var generatedPassword = string.IsNullOrWhiteSpace(password);
        password = generatedPassword
            ? $"DevTest9{Convert.ToHexString(RandomNumberGenerator.GetBytes(18))}"
            : password;

        for (var userIndex = 0; userIndex < DemoEmails.Length; userIndex++)
        {
            var email = DemoEmails[userIndex];
            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                user = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
                var createResult = await userManager.CreateAsync(user, password!);
                EnsureSucceeded(createResult, $"Could not create Development account '{email}'.");
            }
            else if (generatedPassword || !await userManager.CheckPasswordAsync(user, password!))
            {
                var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
                var resetResult = await userManager.ResetPasswordAsync(user, resetToken, password!);
                EnsureSucceeded(resetResult, $"Could not reset Development account '{email}'.");
            }

            if (generatedPassword)
            {
                logger.LogWarning("Development-only test account: {Email}; generated password: {Password}", email, password);
            }

            var products = catalog.Products;
            var firstProduct = products[userIndex % products.Count];
            var secondProduct = products[(userIndex + 1) % products.Count];
            var sampleOrders = new[]
            {
                (Suffix: "approved", Status: "approved", Product: firstProduct, Quantity: 1),
                (Suffix: "rejected", Status: "rejected", Product: secondProduct, Quantity: 1),
                (Suffix: "pending", Status: "pending", Product: firstProduct, Quantity: 2)
            };

            foreach (var sample in sampleOrders)
            {
                var externalReference = $"development-sample-{user.Id}-{sample.Suffix}";
                if (await database.PaymentOrders.AnyAsync(order => order.ExternalReference == externalReference, cancellationToken))
                {
                    continue;
                }

                var order = new PaymentOrder
                {
                    ExternalReference = externalReference,
                    PreferenceId = "development-sample",
                    Status = sample.Status,
                    StatusDetail = "Development sample data",
                    UserId = user.Id,
                    Amount = sample.Product.Price * sample.Quantity,
                    Currency = "ARS",
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-(userIndex * 3 + Array.IndexOf(sampleOrders, sample) + 1)),
                    ApprovedAtUtc = sample.Status == "approved" ? DateTime.UtcNow.AddDays(-1) : null,
                    IsDevelopmentSample = true,
                    Items =
                    [
                        new PaymentOrderItem
                        {
                            ProductId = sample.Product.Id,
                            ProductName = sample.Product.Name,
                            Quantity = sample.Quantity,
                            UnitPrice = sample.Product.Price
                        }
                    ]
                };

                database.PaymentOrders.Add(order);
            }
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"{message} {string.Join("; ", result.Errors.Select(error => error.Description))}");
        }
    }
}