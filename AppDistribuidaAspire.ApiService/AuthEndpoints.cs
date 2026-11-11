using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace AppDistribuidaAspire.ApiService;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth");
        group.MapIdentityApi<IdentityUser>();
        group.MapGet("/me", async (ClaimsPrincipal principal, UserManager<IdentityUser> userManager) =>
        {
            var user = await userManager.GetUserAsync(principal);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new { user.Id, user.Email });
        }).RequireAuthorization();

        return app;
    }
}
