using System.Net;
using System.Text;
using System.Text.Json;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AppDistribuidaAspire.ApiService.Tests.Payments;

public sealed class MercadoPagoCheckoutServiceTests
{
    [Fact]
    public async Task SandboxPreference_UsesInitPoint_AndExpectedOrderDetails()
    {
        var handler = new StubHttpMessageHandler("""
            {"id":"preference-123","init_point":"https://www.mercadopago.com/checkout/live","sandbox_init_point":"https://sandbox.mercadopago.com/checkout/test"}
            """);
        var service = CreateService(handler, accessToken: "APP_USR-unit-test-token", useSandbox: true);

        var result = await service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 2)],
            "order-ref-123",
            "https://localhost:7034/",
            CancellationToken.None);

        Assert.Equal("preference-123", result.PreferenceId);
        Assert.Equal("https://www.mercadopago.com/checkout/live", result.CheckoutUrl);
        Assert.Equal("Bearer APP_USR-unit-test-token", handler.Authorization);

        using var requestJson = JsonDocument.Parse(handler.RequestBody!);
        var root = requestJson.RootElement;
        Assert.Equal("order-ref-123", root.GetProperty("external_reference").GetString());
        Assert.Equal("https://localhost:7034/payment/success", root.GetProperty("back_urls").GetProperty("success").GetString());
        Assert.Equal("https://localhost:7034/payment/failure", root.GetProperty("back_urls").GetProperty("failure").GetString());
        Assert.Equal("https://localhost:7034/payment/pending", root.GetProperty("back_urls").GetProperty("pending").GetString());

        // Localhost is not publicly reachable, so auto_return is omitted in development.
        Assert.False(root.TryGetProperty("auto_return", out _));

        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal(1500m, item.GetProperty("unit_price").GetDecimal());
        Assert.Equal("ARS", item.GetProperty("currency_id").GetString());
    }

    [Fact]
    public async Task PublicReturnUrl_EnablesAutoReturn()
    {
        var handler = new StubHttpMessageHandler("""
            {"id":"preference-789","init_point":"https://www.mercadopago.com/checkout/live"}
            """);
        var service = CreateService(
            handler,
            accessToken: "APP_USR-unit-test-token",
            useSandbox: true,
            new TestHostEnvironment { EnvironmentName = Environments.Production });

        await service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 1)],
            "order-ref-123",
            "https://shop.example.com/",
            CancellationToken.None);

        using var requestJson = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("approved", requestJson.RootElement.GetProperty("auto_return").GetString());
        Assert.Equal(
            "https://shop.example.com/payment/success",
            requestJson.RootElement.GetProperty("back_urls").GetProperty("success").GetString());
    }

    [Fact]
    public async Task MissingInitPoint_FallsBackToSandboxInitPoint()
    {
        var handler = new StubHttpMessageHandler("""
            {"id":"preference-456","sandbox_init_point":"https://sandbox.mercadopago.com/checkout/test"}
            """);
        var service = CreateService(handler, accessToken: "APP_USR-unit-test-token", useSandbox: true);

        var result = await service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 1)],
            "order-ref-123",
            "https://localhost:7034/",
            CancellationToken.None);

        Assert.Equal("https://sandbox.mercadopago.com/checkout/test", result.CheckoutUrl);
    }

    [Fact]
    public async Task LegacyTestToken_IsAllowedToReachProvider()
    {
        var handler = new StubHttpMessageHandler("""
            {"id":"preference-123","init_point":"https://www.mercadopago.com/checkout/live"}
            """);
        var service = CreateService(handler, accessToken: "TEST-unit-test-token", useSandbox: true);

        var result = await service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 1)],
            "order-ref-123",
            "https://localhost:7034/",
            CancellationToken.None);

        Assert.Equal("preference-123", result.PreferenceId);
        Assert.NotNull(handler.RequestBody);
        Assert.Equal("Bearer TEST-unit-test-token", handler.Authorization);
    }

    [Fact]
    public async Task ProviderForbidden_IsTranslatedToFriendlyException()
    {
        var handler = new StubHttpMessageHandler(
            """
            {"message":"At least one policy returned UNAUTHORIZED.","blocked_by":"PolicyAgent","status":403,"code":"PA_UNAUTHORIZED_RESULT_FROM_POLICIES"}
            """,
            HttpStatusCode.Forbidden);
        var service = CreateService(handler, accessToken: "APP_USR-unit-test-token", useSandbox: true);

        var exception = await Assert.ThrowsAsync<MercadoPagoException>(() => service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 1)],
            "order-ref-123",
            "https://localhost:7034/",
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal("PA_UNAUTHORIZED_RESULT_FROM_POLICIES", exception.ProviderCode);
        Assert.Contains("bloqueó", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyTestToken_ProducesConfigurationWarning()
    {
        var handler = new StubHttpMessageHandler("{}");
        var service = CreateService(handler, accessToken: "TEST-unit-test-token", useSandbox: true);

        var warning = service.GetConfigurationWarning();

        Assert.NotNull(warning);
        Assert.Contains("APP_USR-", warning, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.CanAttemptCheckout());
    }

    private static MercadoPagoCheckoutService CreateService(
        StubHttpMessageHandler handler,
        string accessToken,
        bool useSandbox,
        IHostEnvironment? environment = null)
    {
        var options = Options.Create(new MercadoPagoOptions
        {
            AccessToken = accessToken,
            UseSandbox = useSandbox,
            SuccessUrl = "https://localhost:7034/payment/success",
            FailureUrl = "https://localhost:7034/payment/failure",
            PendingUrl = "https://localhost:7034/payment/pending"
        });

        return new MercadoPagoCheckoutService(
            new StubHttpClientFactory(handler),
            options,
            environment ?? new TestHostEnvironment(),
            NullLogger<MercadoPagoCheckoutService>.Instance);
    }

    private sealed class StubHttpClientFactory(StubHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.mercadopago.com/")
        };
    }

    private sealed class StubHttpMessageHandler(
        string responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AppDistribuidaAspire.ApiService.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
