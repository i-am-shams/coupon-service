using Microsoft.EntityFrameworkCore;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Infrastructure;

public sealed class PizzaShopDbContext : DbContext
{
    public PizzaShopDbContext(DbContextOptions<PizzaShopDbContext> options) : base(options) { }

    public DbSet<PizzaEntity> Pizzas => Set<PizzaEntity>();
    public DbSet<CouponEntity> Coupons => Set<CouponEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderLineEntity> OrderLines => Set<OrderLineEntity>();
    public DbSet<CouponRedemptionEntity> CouponRedemptions => Set<CouponRedemptionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PizzaShopDbContext).Assembly);
    }
}
