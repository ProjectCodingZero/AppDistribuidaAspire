using AppDistribuidaAspire.ApiService;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
builder.Services.AddHttpClient(nameof(MercadoPagoCheckoutService), client =>
{
    client.BaseAddress = new Uri("https://api.mercadopago.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<MercadoPagoCheckoutService>();
// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<ThisContext>();
    await database.Database.EnsureCreatedAsync();
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

app.MapGroup("/auth")
    .MapIdentityApi<IdentityUser>();

app.MapPost("/api/payments/checkout-preferences", async (
    CreateCheckoutRequest request,
    MercadoPagoCheckoutService checkoutService,
    CancellationToken cancellationToken) =>
{
    if (request.Quantity is < 1 or > 10)
    {
        return Results.BadRequest(new { error = "La cantidad debe estar entre 1 y 10." });
    }

    if (!CheckoutCatalog.TryGetProduct(request.ProductId, out var product))
    {
        return Results.BadRequest(new { error = "El producto seleccionado no existe." });
    }

    try
    {
        var preference = await checkoutService.CreatePreferenceAsync(product, request.Quantity, cancellationToken);
        return Results.Ok(preference);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (HttpRequestException)
    {
        return Results.Problem("No se pudo crear la preferencia de pago con Mercado Pago.", statusCode: StatusCodes.Status502BadGateway);
    }
});

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
