using Microsoft.EntityFrameworkCore;
using PizzaShop.Ordering;

namespace PizzaShop.Infrastructure.Adapters;

/// <summary>
/// Implements the IMenu interface used by Basket.FromLinesAsync, backed by the
/// Pizzas table. Prices are read fresh per request; they never come from the client.
/// </summary>
public sealed class DatabaseMenu : IMenu
{
    private readonly PizzaShopDbContext _db;

    public DatabaseMenu(PizzaShopDbContext db) => _db = db;

    public async Task<decimal?> GetUnitPriceAsync(int pizzaId, CancellationToken cancellationToken = default) =>
        await _db.Pizzas
            .AsNoTracking()
            .Where(p => p.Id == pizzaId)
            .Select(p => (decimal?)p.Price)
            .FirstOrDefaultAsync(cancellationToken);
}
