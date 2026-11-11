using AppDistribuidaAspire.ApiService;
using AppDistribuidaAspire.ApiService.Data;
using AppDistribuidaAspire.ApiService.Payments;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (health checks, telemetry, service discovery).
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
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.Configure<MercadoPagoOptions>(builder.Configuration.GetSection("MercadoPago"));
builder.Services.AddSingleton<ProductCatalog>();
builder.Services.AddScoped<CatalogStockService>();
builder.Services.AddHttpClient(nameof(MercadoPagoCheckoutService), client =>
{
    client.BaseAddress = new Uri("https://api.mercadopago.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<MercadoPagoCheckoutService>();
builder.Services.AddScoped<MercadoPagoPaymentVerificationService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<ThisContext>();
    await DatabaseSchemaInitializer.InitializeAsync(database);
    await scope.ServiceProvider.GetRequiredService<CatalogStockService>().EnsureInitializedAsync();
    if (app.Environment.IsDevelopment())
    {
        await DevelopmentDataSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, app.Logger);
    }
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => "Nexo API is running.");

app.MapAuthEndpoints();
app.MapCatalogEndpoints();
app.MapPaymentEndpoints();
app.MapAccountEndpoints();

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

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
