using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PizzaShop.Infrastructure;

/// <summary>
/// Used by the EF Core tooling (dotnet ef migrations add) when no host is running.
/// LocalDB is used for local development — no Docker required; fastest migration cycle.
/// </summary>
public sealed class PizzaShopDbContextFactory : IDesignTimeDbContextFactory<PizzaShopDbContext>
{
    public PizzaShopDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PizzaShopDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\mssqllocaldb;Database=PizzaShop;Trusted_Connection=True;",
                sql => sql.MigrationsAssembly(typeof(PizzaShopDbContext).Assembly.FullName))
            .Options;

        return new PizzaShopDbContext(options);
    }
}
