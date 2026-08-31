using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PizzaShop.Infrastructure;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Bdd;

/// <summary>
/// Hosts the API in-process with an in-memory EF Core database.
/// A new database instance is created per factory (per test that uses it).
/// </summary>
public sealed class PizzaShopWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly IReadOnlyList<PizzaEntity>? _pizzas;
    private readonly IReadOnlyList<CouponEntity>? _coupons;

    public PizzaShopWebApplicationFactory(
        IReadOnlyList<PizzaEntity>? pizzas = null,
        IReadOnlyList<CouponEntity>? coupons = null)
    {
        _pizzas = pizzas;
        _coupons = coupons;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace the real SQL Server context with an in-memory one.
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<PizzaShopDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<PizzaShopDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));
        });

        builder.UseEnvironment("Test");
    }

    /// <summary>
    /// Seeds the in-memory database with the given data before the first request.
    /// Must be called before CreateClient() or GetClient() is first used.
    /// </summary>
    public void SeedDatabase(
        IReadOnlyList<PizzaEntity>? pizzas = null,
        IReadOnlyList<CouponEntity>? coupons = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();
        db.Database.EnsureCreated();

        var p = pizzas ?? _pizzas ?? Array.Empty<PizzaEntity>();
        var c = coupons ?? _coupons ?? Array.Empty<CouponEntity>();

        if (p.Any()) db.Pizzas.AddRange(p);
        if (c.Any()) db.Coupons.AddRange(c);

        db.SaveChanges();
    }
}
