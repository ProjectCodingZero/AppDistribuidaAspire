using Microsoft.EntityFrameworkCore;

namespace AppDistribuidaAspire.ApiService;

public class ThisContext : DbContext
{
    public ThisContext(DbContextOptions<ThisContext> options)
       : base(options)
    { }

}
