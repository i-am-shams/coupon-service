using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PizzaShop.Infrastructure;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Bdd;

/// <summary>
/// Hosts the API in-process against a SQLite in-memory database.
/// A new database is created per factory, so scenarios cannot see each other's data.
/// </summary>
/// <remarks>
/// SQLite rather than the EF in-memory provider, because the in-memory provider
/// supports neither <c>ExecuteUpdateAsync</c> nor transactions — which are exactly
/// what rule 4's atomic redemption and the order transaction are built on. Under the
/// in-memory provider every order carrying a coupon returned a 500, and the suite
/// passed only because no scenario sent one. See docs/decisions.md.
///
/// The connection is held open deliberately: a SQLite in-memory database exists only
/// while at least one connection to it is open.
/// </remarks>
public sealed class PizzaShopWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IReadOnlyList<PizzaEntity>? _pizzas;
    private readonly IReadOnlyList<CouponEntity>? _coupons;

    public PizzaShopWebApplicationFactory(
        IReadOnlyList<PizzaEntity>? pizzas = null,
        IReadOnlyList<CouponEntity>? coupons = null)
    {
        _pizzas = pizzas;
        _coupons = coupons;
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace the real SQL Server context with the SQLite one.
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<PizzaShopDbContext>));
            if (descriptor is not null) services.Remove(descriptor);

            services.AddDbContext<PizzaShopDbContext>(options => options.UseSqlite(_connection));
        });

        // Program.cs skips startup migrations in this environment: the migrations are
        // SQL Server specific, so the schema is created from the model instead.
        builder.UseEnvironment("Test");
    }

    /// <summary>
    /// Creates the schema and seeds the given data.
    /// Must be called before the first request that reads it.
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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
