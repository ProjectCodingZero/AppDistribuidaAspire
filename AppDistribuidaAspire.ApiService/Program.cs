using AppDistribuidaAspire.ApiService;
using AppDistribuidaAspire.ApiService.Data;
using AppDistribuidaAspire.ApiService.Payments;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();
builder.Services.AddDbContext<ThisContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("The DefaultConnection connection string is required.");

    options.UseSqlite(connectionString);
});

builder.Services.AddIdentityApiEndpoints<IdentityUser>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 10;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
    .AddEntityFrameworkStores<ThisContext>();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.Configure<MercadoPagoOptions>(builder.Configuration.GetSection("MercadoPago"));
builder.Services.AddSingleton<ProductCatalog>();
builder.Services.AddHttpClient(nameof(MercadoPagoCheckoutService), client =>
{
    client.BaseAddress = new Uri("https://api.mercadopago.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<MercadoPagoCheckoutService>();
builder.Services.AddScoped<MercadoPagoPaymentVerificationService>();
// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<ThisContext>();
    await DatabaseSchemaInitializer.InitializeAsync(database);
    if (app.Environment.IsDevelopment())
    {
        await DevelopmentDataSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
    }
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/", () => "API service is running. Navigate to /weatherforecast to see sample data.");

var authEndpoints = app.MapGroup("/auth");
authEndpoints.MapIdentityApi<IdentityUser>();
authEndpoints.MapGet("/me", async (ClaimsPrincipal principal, UserManager<IdentityUser> userManager) =>
{
    var user = await userManager.GetUserAsync(principal);
    return user is null
        ? Results.Unauthorized()
        : Results.Ok(new { user.Id, user.Email });
}).RequireAuthorization();

app.MapGet("/api/products", (ProductCatalog catalog) => Results.Ok(catalog.Products));

app.MapGet("/api/payments/checkout-config", (IOptions<MercadoPagoOptions> configuredOptions) =>
{
    var options = configuredOptions.Value;
    var mode = string.Equals(options.CheckoutMode, "Demo", StringComparison.OrdinalIgnoreCase)
        ? "Demo"
        : "MercadoPago";

    return Results.Ok(new { Mode = mode, AutoApprove = options.AutoApproveCheckout });
});

app.MapPost("/api/payments/checkout-preferences", async (
    CreateCheckoutRequest request,
    ClaimsPrincipal principal,
    ProductCatalog catalog,
    MercadoPagoCheckoutService checkoutService,
    IOptions<MercadoPagoOptions> paymentOptions,
    ThisContext database,
    CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    await PendingOrderExpiration.CancelExpiredAsync(database, cancellationToken);

    if (request.Items is null || request.Items.Count == 0 || request.Items.Count > 20)
    {
        return Results.BadRequest(new { error = "El carrito debe contener entre 1 y 20 productos distintos." });
    }

    if (request.Items.Select(item => item.ProductId).Distinct().Count() != request.Items.Count)
    {
        return Results.BadRequest(new { error = "El carrito contiene productos duplicados." });
    }

    var checkoutLines = new List<CheckoutLine>();
    foreach (var item in request.Items)
    {
        if (item.Quantity is < 1 or > 10)
        {
            return Results.BadRequest(new { error = "Cada cantidad debe estar entre 1 y 10." });
        }

        if (!catalog.TryGetProduct(item.ProductId, out var product))
        {
            return Results.BadRequest(new { error = "El carrito contiene un producto inexistente." });
        }

        if (item.Quantity > product.Stock)
        {
            return Results.BadRequest(new { error = $"No hay stock suficiente para {product.Name}." });
        }

        checkoutLines.Add(new CheckoutLine(product, item.Quantity));
    }

    var externalReference = Guid.NewGuid().ToString("N");
    var order = new PaymentOrder
    {
        ExternalReference = externalReference,
        Status = "creating_preference",
        UserId = userId,
        Amount = checkoutLines.Sum(line => line.Product.Price * line.Quantity),
        Currency = "ARS",
        CreatedAtUtc = DateTime.UtcNow,
        Items = checkoutLines.Select(line => new PaymentOrderItem
        {
            ProductId = line.Product.Id,
            ProductName = line.Product.Name,
            Quantity = line.Quantity,
            UnitPrice = line.Product.Price
        }).ToList()
    };
    database.PaymentOrders.Add(order);
    await database.SaveChangesAsync(cancellationToken);

    var options = paymentOptions.Value;
    var demoMode = string.Equals(options.CheckoutMode, "Demo", StringComparison.OrdinalIgnoreCase);

    // Demo mode: approve the purchase right away so the store is always usable
    // without Mercado Pago credentials or a test buyer account.
    if (demoMode)
    {
        ApproveAsDemo(order);
        order.CheckoutUrl = CreateDemoCheckout(order).CheckoutUrl;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(CreateDemoCheckout(order));
    }

    try
    {
        var preference = await checkoutService.CreatePreferenceAsync(checkoutLines, externalReference, cancellationToken);
        order.PreferenceId = preference.PreferenceId;
        order.CheckoutUrl = preference.CheckoutUrl;

        if (options.AutoApproveCheckout)
        {
            order.Status = "approved";
            order.StatusDetail = "Aprobado automáticamente al iniciar el checkout";
            order.ApprovedAtUtc = DateTime.UtcNow;
        }
        else
        {
            order.Status = "pending";
        }

        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(preference);
    }
    catch (InvalidOperationException) when (options.AutoApproveCheckout)
    {
        // Mercado Pago is misconfigured, but the demo must keep working.
        ApproveAsDemo(order);
        order.CheckoutUrl = CreateDemoCheckout(order).CheckoutUrl;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(CreateDemoCheckout(order));
    }
    catch (HttpRequestException) when (options.AutoApproveCheckout)
    {
        ApproveAsDemo(order);
        order.CheckoutUrl = CreateDemoCheckout(order).CheckoutUrl;
        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(CreateDemoCheckout(order));
    }
    catch (InvalidOperationException exception)
    {
        order.Status = "checkout_error";
        await database.SaveChangesAsync(cancellationToken);
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        order.Status = "checkout_error";
        await database.SaveChangesAsync(cancellationToken);
        return Results.Problem("No se pudo crear la preferencia de pago con Mercado Pago.", statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireAuthorization();

static void ApproveAsDemo(PaymentOrder order)
{
    order.PreferenceId = "demo";
    order.Status = "approved";
    order.StatusDetail = "Compra aprobada automáticamente (modo de prueba)";
    order.ApprovedAtUtc = DateTime.UtcNow;
}

static CreateCheckoutResponse CreateDemoCheckout(PaymentOrder order) =>
    new(order.PreferenceId, $"/payment/success?external_reference={Uri.EscapeDataString(order.ExternalReference)}");

app.MapPost("/api/payments/orders/{externalReference}/retry-checkout", async (
    string externalReference,
    ClaimsPrincipal principal,
    IOptions<MercadoPagoOptions> paymentOptions,
    MercadoPagoCheckoutService checkoutService,
    ThisContext database,
    CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    await PendingOrderExpiration.CancelExpiredAsync(database, cancellationToken);

    var order = await database.PaymentOrders
        .Include(candidate => candidate.Items)
        .SingleOrDefaultAsync(candidate =>
            candidate.ExternalReference == externalReference && candidate.UserId == userId,
            cancellationToken);
    if (order is null)
    {
        return Results.NotFound(new { error = "No encontramos ese pedido en tu cuenta." });
    }

    switch (order.Status.ToLowerInvariant())
    {
        case "pending":
        case "in_process":
        case "creating_preference":
            break;
        case "approved":
            return Results.Conflict(new { error = "Esta compra ya está aprobada." });
        case "cancelled":
            return Results.Conflict(new { error = "El pedido expiró a los 5 minutos sin pago y quedó cancelado. Volvé a armar tu carrito." });
        default:
            return Results.Conflict(new { error = "Este pedido ya no se puede pagar. Iniciá una nueva compra desde el carrito." });
    }

    // While the order is inside the payment window the stored checkout link stays valid.
    if (!string.IsNullOrWhiteSpace(order.CheckoutUrl))
    {
        return Results.Ok(new CreateCheckoutResponse(order.PreferenceId, order.CheckoutUrl));
    }

    // Preference creation failed earlier: retry with the same products and external reference.
    var options = paymentOptions.Value;
    var checkoutLines = order.Items.Select(item => new CheckoutLine(
        new CatalogProduct(item.ProductId, item.ProductName, string.Empty, string.Empty, string.Empty, item.UnitPrice, 1, string.Empty),
        item.Quantity)).ToList();

    try
    {
        var preference = await checkoutService.CreatePreferenceAsync(checkoutLines, order.ExternalReference, cancellationToken);
        order.PreferenceId = preference.PreferenceId;
        order.CheckoutUrl = preference.CheckoutUrl;

        if (options.AutoApproveCheckout)
        {
            order.Status = "approved";
            order.StatusDetail = "Aprobado automáticamente al iniciar el checkout";
            order.ApprovedAtUtc = DateTime.UtcNow;
        }
        else
        {
            order.Status = "pending";
            order.StatusDetail = null;
            order.ApprovedAtUtc = null;
        }

        await database.SaveChangesAsync(cancellationToken);
        return Results.Ok(preference);
    }
    catch (InvalidOperationException)
    {
        return Results.Problem(
            "Mercado Pago no está configurado para retomar este pedido. Volvé a armar tu carrito.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            "No se pudo reiniciar el pago con Mercado Pago. Intentá de nuevo en unos minutos.",
            statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireAuthorization();

app.MapPost("/api/payments/webhook", async (
    HttpRequest request,
    IOptions<MercadoPagoOptions> configuredOptions,
    MercadoPagoPaymentVerificationService verificationService,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var options = configuredOptions.Value;
    if (string.IsNullOrWhiteSpace(options.WebhookSecret))
    {
        return Results.Problem("Mercado Pago webhook is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var paymentId = request.Query["data.id"].FirstOrDefault() ?? request.Query["id"].FirstOrDefault();
    if (!MercadoPagoWebhookSignatureValidator.IsValid(
            request.Headers["x-signature"].FirstOrDefault(),
            request.Headers["x-request-id"].FirstOrDefault(),
            paymentId,
            options.WebhookSecret))
    {
        return Results.Unauthorized();
    }

    try
    {
        var handled = await verificationService.ProcessWebhookAsync(paymentId!, cancellationToken);
        if (!handled)
        {
            logger.LogWarning("Mercado Pago webhook payment {PaymentId} did not match a pending local order.", paymentId);
        }

        return Results.Ok();
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Could not retrieve the payment from Mercado Pago.", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException)
    {
        return Results.Problem("Mercado Pago credentials are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest();
    }
}).AllowAnonymous();

app.MapGet("/api/payments/return/{externalReference}", async (
    string externalReference,
    ClaimsPrincipal principal,
    MercadoPagoPaymentVerificationService verificationService,
    CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    try
    {
        var payment = await verificationService.GetOrderStatusAsync(externalReference, userId, cancellationToken);
        return payment is null ? Results.NotFound() : Results.Ok(payment);
    }
    catch (InvalidOperationException)
    {
        return Results.Problem("Mercado Pago credentials are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("Could not verify the payment with Mercado Pago.", statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireAuthorization();

app.MapGet("/api/payments/verify/{paymentId}", async (
    string paymentId,
    ClaimsPrincipal principal,
    MercadoPagoPaymentVerificationService verificationService,
    CancellationToken cancellationToken) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    try
    {
        var payment = await verificationService.VerifyAsync(paymentId, userId, cancellationToken);
        return payment is null ? Results.NotFound() : Results.Ok(payment);
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "El identificador del pago no es válido." });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("No se pudo verificar el pago con Mercado Pago.", statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireAuthorization();

app.MapGet("/api/account/purchases", async (
    ClaimsPrincipal principal,
    ThisContext database,
    CancellationToken cancellationToken) =>
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
}).RequireAuthorization();

app.MapGet("/api/account/purchases/{externalReference}", async (
    string externalReference,
    ClaimsPrincipal principal,
    ThisContext database,
    CancellationToken cancellationToken) =>
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
}).RequireAuthorization();

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");
app.MapDefaultEndpoints();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
