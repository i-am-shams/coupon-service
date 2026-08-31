using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Infrastructure;

/// <summary>
/// Seeds the menu and reference coupons. Called after migrations run at startup.
/// Idempotent: checks before inserting.
/// </summary>
public static class PizzaShopDbSeeder
{
    public static async Task SeedAsync(PizzaShopDbContext db)
    {
        await SeedPizzasAsync(db);
        await SeedCouponsAsync(db);
        await db.SaveChangesAsync();
    }

    private static Task SeedPizzasAsync(PizzaShopDbContext db)
    {
        if (db.Pizzas.Any()) return Task.CompletedTask;

        db.Pizzas.AddRange(
            new PizzaEntity { Name = "Margherita",        Description = "Tomato, mozzarella, fresh basil",                       Price = 10.00m },
            new PizzaEntity { Name = "Pepperoni",         Description = "Tomato, mozzarella, pepperoni",                         Price = 12.00m },
            new PizzaEntity { Name = "Veggie Supreme",    Description = "Tomato, mozzarella, peppers, mushrooms, red onion",     Price = 11.50m },
            new PizzaEntity { Name = "BBQ Chicken",       Description = "BBQ sauce, mozzarella, chicken, red onion",             Price = 13.50m },
            new PizzaEntity { Name = "Four Cheese",       Description = "Mozzarella, cheddar, parmesan, gorgonzola",             Price = 13.00m },
            new PizzaEntity { Name = "Diavola",           Description = "Tomato, mozzarella, spicy salami, chilli",              Price = 12.50m },
            new PizzaEntity { Name = "Prosciutto",        Description = "Tomato, mozzarella, Parma ham, rocket",                 Price = 14.00m },
            new PizzaEntity { Name = "Truffle Funghi",    Description = "Truffle oil, mozzarella, mixed mushrooms, thyme",       Price = 15.00m }
        );
        return Task.CompletedTask;
    }

    private static Task SeedCouponsAsync(PizzaShopDbContext db)
    {
        if (db.Coupons.Any()) return Task.CompletedTask;

        // Future expiry used throughout — 10 years from the time the seed runs so the
        // tests still pass after a date roll-over.
        var future = DateTimeOffset.UtcNow.AddYears(10);
        var past   = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        db.Coupons.AddRange(
            // Valid percentage discount — used by BDD "preview" and general scenarios.
            new CouponEntity
            {
                Code             = "PIZZA10",
                Type             = CouponTypeEntity.Percentage,
                Value            = 10m,
                Description      = "10% off your order",
                ExpiresAt        = future,
                MinimumOrderValue = 0m,
                RedemptionLimit  = 100,
                UsageCount       = 0,
            },
            // Valid fixed-amount discount.
            new CouponEntity
            {
                Code             = "FIVEOFF",
                Type             = CouponTypeEntity.FixedAmount,
                Value            = 5m,
                Description      = "€5 off your order",
                ExpiresAt        = future,
                MinimumOrderValue = 0m,
                RedemptionLimit  = 100,
                UsageCount       = 0,
            },
            // Expired coupon — exercises the Expired rejection path.
            new CouponEntity
            {
                Code             = "OLDCODE",
                Type             = CouponTypeEntity.Percentage,
                Value            = 10m,
                Description      = "10% off (expired)",
                ExpiresAt        = past,
                MinimumOrderValue = 0m,
                RedemptionLimit  = 100,
                UsageCount       = 0,
            },
            // Minimum-spend coupon — exercises the MinimumSpendNotMet rejection path.
            new CouponEntity
            {
                Code             = "SPEND50",
                Type             = CouponTypeEntity.Percentage,
                Value            = 15m,
                Description      = "15% off orders over €50",
                ExpiresAt        = future,
                MinimumOrderValue = 50m,
                RedemptionLimit  = 100,
                UsageCount       = 0,
            }
        );
        return Task.CompletedTask;
    }
}
