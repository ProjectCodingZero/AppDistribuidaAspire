using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AppDistribuidaAspire.ApiService.Payments;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments");

        group.MapGet("/checkout-config", GetCheckoutConfig);
        group.MapPost("/checkout-preferences", CreateCheckoutAsync).RequireAuthorization();
        group.MapPost("/orders/{externalReference}/retry-checkout", RetryCheckoutAsync).RequireAuthorization();
        group.MapPost("/webhook", HandleWebhookAsync).AllowAnonymous();
        group.MapGet("/return/{externalReference}", GetOrderStatusAsync).RequireAuthorization();
        group.MapGet("/verify/{paymentId}", VerifyPaymentAsync).RequireAuthorization();

        return app;
    }

    private static IResult GetCheckoutConfig(
        IOptions<MercadoPagoOptions> configuredOptions,
        MercadoPagoCheckoutService checkoutService)
    {
        var options = configuredOptions.Value;
        var isDemo = string.Equals(options.CheckoutMode, "Demo", StringComparison.OrdinalIgnoreCase);
        var warning = isDemo ? null : checkoutService.GetConfigurationWarning();

        return Results.Ok(new CheckoutConfigResponse(
            isDemo ? "Demo" : "MercadoPago",
            options.AutoApproveCheckout,
            Ready: isDemo || checkoutService.CanAttemptCheckout(),
            Warning: warning));
    }

    private static async Task<IResult> CreateCheckoutAsync(
        CreateCheckoutRequest request,
        ClaimsPrincipal principal,
        CatalogStockService stockService,
        MercadoPagoCheckoutService checkoutService,
        IOptions<MercadoPagoOptions> paymentOptions,
        ThisContext database,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
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

            var product = await stockService.GetProductAsync(item.ProductId, cancellationToken);
            if (product is null)
            {
                return Results.BadRequest(new { error = "El carrito contiene un producto inexistente." });
            }

            if (item.Quantity > product.Stock)
            {
                return Results.BadRequest(new { error = $"No hay stock suficiente para {product.Name}. Quedan {product.Stock} unidades." });
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
            await ApplyStockOrWarnAsync(stockService, order, database, logger, cancellationToken);
            return Results.Ok(CreateDemoCheckout(order));
        }

        try
        {
            var preference = await checkoutService.CreatePreferenceAsync(
                checkoutLines,
                externalReference,
                request.ReturnBaseUrl,
                cancellationToken);

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
            }

            await database.SaveChangesAsync(cancellationToken);
            if (options.AutoApproveCheckout)
            {
                await ApplyStockOrWarnAsync(stockService, order, database, logger, cancellationToken);
            }

            return Results.Ok(preference);
        }
        catch (MercadoPagoException exception) when (options.AutoApproveCheckout)
        {
            logger.LogWarning(exception, "Mercado Pago failed in auto-approve mode. Falling back to demo checkout.");
            return await FallbackToDemoAsync(database, order, stockService, logger, cancellationToken);
        }
        catch (InvalidOperationException) when (options.AutoApproveCheckout)
        {
            logger.LogWarning("Mercado Pago is misconfigured in auto-approve mode. Falling back to demo checkout.");
            return await FallbackToDemoAsync(database, order, stockService, logger, cancellationToken);
        }
        catch (HttpRequestException exception) when (options.AutoApproveCheckout)
        {
            logger.LogWarning(exception, "Mercado Pago is unreachable in auto-approve mode. Falling back to demo checkout.");
            return await FallbackToDemoAsync(database, order, stockService, logger, cancellationToken);
        }
        catch (MercadoPagoException exception)
        {
            order.Status = "checkout_error";
            order.StatusDetail = exception.Message;
            await database.SaveChangesAsync(cancellationToken);

            var statusCode = exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;

            return Results.Problem(exception.Message, statusCode: statusCode);
        }
        catch (InvalidOperationException exception)
        {
            order.Status = "checkout_error";
            order.StatusDetail = exception.Message;
            await database.SaveChangesAsync(cancellationToken);
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Could not reach Mercado Pago while creating a preference.");
            order.Status = "checkout_error";
            order.StatusDetail = "No se pudo conectar con Mercado Pago.";
            await database.SaveChangesAsync(cancellationToken);
            return Results.Problem(
                "No se pudo conectar con Mercado Pago. Revisá tu conexión e intentá nuevamente.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> RetryCheckoutAsync(
        string externalReference,
        RetryCheckoutRequest request,
        ClaimsPrincipal principal,
        CatalogStockService stockService,
        IOptions<MercadoPagoOptions> paymentOptions,
        MercadoPagoCheckoutService checkoutService,
        ThisContext database,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
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
            case "checkout_error":
                break;
            case "approved":
                return Results.Conflict(new { error = "Esta compra ya está aprobada." });
            case "cancelled":
                return Results.Conflict(new { error = "El pedido expiró a los 5 minutos sin pago y quedó cancelado. Volvé a armar tu carrito." });
            default:
                return Results.Conflict(new { error = "Este pedido ya no se puede pagar. Iniciá una nueva compra desde el carrito." });
        }

        // While the order is inside the payment window the stored checkout link stays valid.
        // Orders that failed to create a preference must always build a fresh one.
        if (!string.Equals(order.Status, "checkout_error", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(order.CheckoutUrl))
        {
            return Results.Ok(new CreateCheckoutResponse(order.PreferenceId, order.CheckoutUrl));
        }

        // Preference creation failed earlier: retry with the same products and external reference.
        var options = paymentOptions.Value;
        foreach (var item in order.Items)
        {
            var product = await stockService.GetProductAsync(item.ProductId, cancellationToken);
            if (product is null || product.Stock < item.Quantity)
            {
                return Results.Conflict(new
                {
                    error = $"No hay stock suficiente para {item.ProductName}. Volvé a armar tu carrito."
                });
            }
        }

        var checkoutLines = order.Items.Select(item => new CheckoutLine(
            new CatalogProduct(item.ProductId, item.ProductName, string.Empty, string.Empty, string.Empty, item.UnitPrice, 1, string.Empty),
            item.Quantity)).ToList();

        try
        {
            var preference = await checkoutService.CreatePreferenceAsync(
                checkoutLines,
                order.ExternalReference,
                request.ReturnBaseUrl,
                cancellationToken);

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
            if (options.AutoApproveCheckout)
            {
                await ApplyStockOrWarnAsync(stockService, order, database, logger, cancellationToken);
            }

            return Results.Ok(preference);
        }
        catch (MercadoPagoException exception)
        {
            order.StatusDetail = exception.Message;
            await database.SaveChangesAsync(cancellationToken);

            var statusCode = exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;

            return Results.Problem(exception.Message, statusCode: statusCode);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                "No se pudo reiniciar el pago con Mercado Pago. Intentá de nuevo en unos minutos.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpRequest request,
        IOptions<MercadoPagoOptions> configuredOptions,
        MercadoPagoPaymentVerificationService verificationService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
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
    }

    private static async Task<IResult> GetOrderStatusAsync(
        string externalReference,
        ClaimsPrincipal principal,
        MercadoPagoPaymentVerificationService verificationService,
        CancellationToken cancellationToken)
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
    }

    private static async Task<IResult> VerifyPaymentAsync(
        string paymentId,
        ClaimsPrincipal principal,
        MercadoPagoPaymentVerificationService verificationService,
        CancellationToken cancellationToken)
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
    }

    private static async Task<IResult> FallbackToDemoAsync(
        ThisContext database,
        PaymentOrder order,
        CatalogStockService stockService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        ApproveAsDemo(order);
        order.CheckoutUrl = CreateDemoCheckout(order).CheckoutUrl;
        await database.SaveChangesAsync(cancellationToken);
        await ApplyStockOrWarnAsync(stockService, order, database, logger, cancellationToken);
        return Results.Ok(CreateDemoCheckout(order));
    }

    private static async Task ApplyStockOrWarnAsync(
        CatalogStockService stockService,
        PaymentOrder order,
        ThisContext database,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (await stockService.TryApplyStockAsync(order, cancellationToken))
        {
            return;
        }

        order.StatusDetail = "El pago se aprobó, pero no había stock suficiente para descontarlo.";
        await database.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Order {ExternalReference} was approved without enough stock to discount.",
            order.ExternalReference);
    }

    private static void ApproveAsDemo(PaymentOrder order)
    {
        order.PreferenceId = "demo";
        order.Status = "approved";
        order.StatusDetail = "Compra aprobada automáticamente (modo de prueba)";
        order.ApprovedAtUtc = DateTime.UtcNow;
    }

    private static CreateCheckoutResponse CreateDemoCheckout(PaymentOrder order) =>
        new(order.PreferenceId, $"/payment/success?external_reference={Uri.EscapeDataString(order.ExternalReference)}");
}
