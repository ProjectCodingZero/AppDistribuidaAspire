using System.Net;
using System.Text;
using System.Text.Json;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AppDistribuidaAspire.ApiService.Tests.Payments;

public sealed class MercadoPagoCheckoutServiceTests
{
    [Fact]
    public async Task SandboxPreference_UsesSandboxUrl_AndExpectedOrderDetails()
    {
        var handler = new StubHttpMessageHandler("""
            {"id":"preference-123","init_point":"https://www.mercadopago.com/checkout/live","sandbox_init_point":"https://sandbox.mercadopago.com/checkout/test"}
            """);
        var service = CreateService(handler, accessToken: "TEST-unit-test-token", useSandbox: true);

        var result = await service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 2)],
            "order-ref-123",
            CancellationToken.None);

        Assert.Equal("preference-123", result.PreferenceId);
        Assert.Equal("https://sandbox.mercadopago.com/checkout/test", result.CheckoutUrl);
        Assert.Equal("Bearer TEST-unit-test-token", handler.Authorization);

        using var requestJson = JsonDocument.Parse(handler.RequestBody!);
        var root = requestJson.RootElement;
        Assert.Equal("order-ref-123", root.GetProperty("external_reference").GetString());
        Assert.Equal("https://localhost:7034/payment/success", root.GetProperty("back_urls").GetProperty("success").GetString());
        Assert.Equal("https://localhost:7034/payment/failure", root.GetProperty("back_urls").GetProperty("failure").GetString());
        Assert.Equal("https://localhost:7034/payment/pending", root.GetProperty("back_urls").GetProperty("pending").GetString());

        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal(1500m, item.GetProperty("unit_price").GetDecimal());
        Assert.Equal("ARS", item.GetProperty("currency_id").GetString());
    }

    [Fact]
    public async Task SandboxPreference_RejectsProductionAccessTokenBeforeCallingProvider()
    {
        var handler = new StubHttpMessageHandler("{}");
        var service = CreateService(handler, accessToken: "APP_USR-production-token", useSandbox: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreatePreferenceAsync(
            [new CheckoutLine(new CatalogProduct(7, "Libro", "Libros", "Editorial", "Descripción", 1500m, 4, "https://example.test/libro"), 1)],
            "order-ref-123",
            CancellationToken.None));

        Assert.Contains("sandbox", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.RequestBody);
    }

    private static MercadoPagoCheckoutService CreateService(
        StubHttpMessageHandler handler,
        string accessToken,
        bool useSandbox)
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
            new TestHostEnvironment());
    }

    private sealed class StubHttpClientFactory(StubHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.mercadopago.com/")
        };
    }

    private sealed class StubHttpMessageHandler(string responseJson) : HttpMessageHandler
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

            return new HttpResponseMessage(HttpStatusCode.OK)
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
