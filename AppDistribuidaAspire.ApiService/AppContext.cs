using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService;

public class ThisContext : IdentityDbContext<IdentityUser>
{
    public ThisContext(DbContextOptions<ThisContext> options)
        : base(options)
    {
    }
}
