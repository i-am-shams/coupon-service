using Microsoft.EntityFrameworkCore;
using PizzaShop.Ordering;

namespace PizzaShop.Infrastructure.Adapters;

/// <summary>
/// Implements the IMenu interface used by Basket.FromLines, backed by the
/// Pizzas table. Prices are read fresh per request; they never come from the client.
/// </summary>
public sealed class DatabaseMenu : IMenu
{
    private readonly PizzaShopDbContext _db;

    public DatabaseMenu(PizzaShopDbContext db) => _db = db;

    public bool TryGetUnitPrice(int pizzaId, out decimal unitPrice)
    {
        var pizza = _db.Pizzas.AsNoTracking().FirstOrDefault(p => p.Id == pizzaId);
        if (pizza is null)
        {
            unitPrice = 0m;
            return false;
        }

        unitPrice = pizza.Price;
        return true;
    }
}
