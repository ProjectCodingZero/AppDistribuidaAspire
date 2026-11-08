using System.Net.Http.Json;
using System.Text.Json;

namespace AppDistribuidaAspire.Web.Services;

public sealed class AuthenticationApiClient(HttpClient httpClient)
{
    public async Task<AccountOperationResult> RegisterAsync(string email, string password)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync("auth/register", new Credentials(email, password));
            if (response.IsSuccessStatusCode)
            {
                return new(true);
            }

            return new(false, await ReadErrorMessageAsync(response));
        }
        catch (HttpRequestException)
        {
            return new(false, "No pudimos conectar con el servicio. Intentá nuevamente.");
        }
    }

    public async Task<AccountOperationResult> LoginAsync(string email, string password)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                "auth/login?useCookies=false",
                new Credentials(email, password));

            if (!response.IsSuccessStatusCode)
            {
                return new(false, "El correo o la contraseña no son correctos.");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                return new(false, "No se pudo iniciar la sesión. Intentá nuevamente.");
            }

            return new(
                true,
                AccessToken: token.AccessToken,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn));
        }
        catch (HttpRequestException)
        {
            return new(false, "No pudimos conectar con el servicio. Intentá nuevamente.");
        }
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = document.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = new List<string>();
                foreach (var error in errors.EnumerateObject())
                {
                    if (error.Value.ValueKind == JsonValueKind.Array)
                    {
                        messages.AddRange(error.Value.EnumerateArray()
                            .Select(message => message.GetString())
                            .OfType<string>());
                    }
                    else if (error.Value.GetString() is { } message)
                    {
                        messages.Add(message);
                    }
                }

                var validationMessage = string.Join(" ", messages);
                if (!string.IsNullOrWhiteSpace(validationMessage))
                {
                    return validationMessage;
                }
            }
        }
        catch (JsonException)
        {
        }

        return "No se pudo crear la cuenta. Revisá los datos e intentá nuevamente.";
    }

    private sealed record Credentials(string Email, string Password);

    private sealed record TokenResponse(string? AccessToken, int ExpiresIn);
}

public sealed record AccountOperationResult(
    bool Succeeded,
    string? ErrorMessage = null,
    string? AccessToken = null,
    DateTimeOffset? ExpiresAt = null);
