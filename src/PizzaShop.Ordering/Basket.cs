namespace PizzaShop.Ordering;

/// <summary>
/// Thrown when a basket references a pizza the menu does not have.
/// Phase D maps this to a 400 with ProblemDetails.
/// </summary>
public sealed class UnknownPizzaException(int pizzaId)
    : Exception($"No pizza with id {pizzaId} exists on the menu.")
{
    public int PizzaId { get; } = pizzaId;
}

/// <summary>
/// A basket line exactly as a client submits it: what, and how many.
/// It carries no money, and there is no field here for one to arrive in.
/// </summary>
public sealed record BasketLine(int PizzaId, int Quantity);

/// <summary>
/// The server's own price data. The only source a basket price may come from.
/// </summary>
public interface IMenu
{
    bool TryGetUnitPrice(int pizzaId, out decimal unitPrice);
}

/// <summary>
/// One priced line. The constructor is deliberately <c>internal</c>: outside this
/// assembly a <see cref="BasketItem"/> cannot be created at all, so no caller can
/// supply its own <see cref="UnitPrice"/>. Prices enter only through
/// <see cref="Basket.FromLines"/>, which reads them from <see cref="IMenu"/>.
/// </summary>
public sealed record BasketItem
{
    internal BasketItem(int pizzaId, int quantity, decimal unitPrice)
    {
        PizzaId = pizzaId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public int PizzaId { get; }
    public int Quantity { get; }
    public decimal UnitPrice { get; }

    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>
/// A basket priced from the server's own data.
/// </summary>
/// <remarks>
/// Rule 1 — the server decides the price — is enforced here by construction rather
/// than by convention. There is no public constructor, so the only way to obtain a
/// Basket is to hand <see cref="FromLines"/> a set of client lines and a menu; the
/// unit prices are then read from the menu and cannot be influenced by the caller.
/// Binding a request DTO straight onto a priced basket is not possible to write.
/// </remarks>
public sealed record Basket
{
    private Basket(IReadOnlyList<BasketItem> items) => Items = items;

    public IReadOnlyList<BasketItem> Items { get; }

    public decimal Subtotal => Items.Sum(i => i.LineTotal);

    /// <summary>
    /// Build a priced basket from client-supplied lines, resolving every price
    /// from <paramref name="menu"/>.
    /// </summary>
    /// <exception cref="UnknownPizzaException">A line names a pizza not on the menu.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A line has a quantity below one.</exception>
    public static Basket FromLines(IEnumerable<BasketLine> lines, IMenu menu)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(menu);

        var items = new List<BasketItem>();

        foreach (var line in lines)
        {
            // A negative or zero quantity would subtract from the subtotal, which is
            // the same class of problem as accepting a price from the client.
            if (line.Quantity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lines),
                    line.Quantity,
                    $"Quantity for pizza {line.PizzaId} must be at least 1.");
            }

            if (!menu.TryGetUnitPrice(line.PizzaId, out var unitPrice))
                throw new UnknownPizzaException(line.PizzaId);

            items.Add(new BasketItem(line.PizzaId, line.Quantity, unitPrice));
        }

        return new Basket(items);
    }
}
