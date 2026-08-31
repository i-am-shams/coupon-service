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
/// <remarks>
/// Asynchronous because the real implementation reads a database. Returns null when
/// the pizza does not exist — an out parameter cannot cross an await.
/// </remarks>
public interface IMenu
{
    Task<decimal?> GetUnitPriceAsync(int pizzaId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One priced line. The constructor is deliberately <c>internal</c>: outside this
/// assembly a <see cref="BasketItem"/> cannot be created at all, so no caller can
/// supply its own <see cref="UnitPrice"/>. Prices enter only through
/// <see cref="Basket.FromLinesAsync"/>, which reads them from <see cref="IMenu"/>.
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
/// Basket is to hand <see cref="FromLinesAsync"/> a set of client lines and a menu; the
/// unit prices are then read from the menu and cannot be influenced by the caller.
/// Binding a request DTO straight onto a priced basket is not possible to write.
/// </remarks>
public sealed record Basket
{
    /// <summary>
    /// The most of any one pizza a single line may carry.
    /// </summary>
    /// <remarks>
    /// Without an upper bound the server prices whatever it is handed: a quantity of
    /// 2,000,000,000 was accepted through the deployed gateway and returned a subtotal of
    /// €20,000,000,000. Nothing overflowed and no price came from the client, so rule 1
    /// held — but an order nobody could fulfil is not a valid order, and the figure is
    /// eventually written to a <c>decimal(18,2)</c> column.
    /// </remarks>
    public const int MaxQuantityPerLine = 50;

    /// <summary>
    /// The most lines a single basket may carry.
    /// </summary>
    /// <remarks>
    /// <see cref="IMenu.GetUnitPriceAsync"/> is one database round trip per line, and for
    /// an order the whole loop runs inside the redemption transaction against a 5 DTU
    /// database. An uncapped line count is therefore a cheap way to hold that transaction
    /// open. The cap bounds it at fifty queries.
    /// </remarks>
    public const int MaxLines = 50;

    private Basket(IReadOnlyList<BasketItem> items) => Items = items;

    public IReadOnlyList<BasketItem> Items { get; }

    public decimal Subtotal => Items.Sum(i => i.LineTotal);

    /// <summary>
    /// Build a priced basket from client-supplied lines, resolving every price
    /// from <paramref name="menu"/>.
    /// </summary>
    /// <exception cref="UnknownPizzaException">A line names a pizza not on the menu.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A line has a quantity below one.</exception>
    public static async Task<Basket> FromLinesAsync(
        IEnumerable<BasketLine> lines,
        IMenu menu,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(menu);

        // Materialised once: the count is needed before the loop, and re-enumerating a
        // lazy sequence would run the caller's projection twice.
        var submitted = lines as IReadOnlyList<BasketLine> ?? lines.ToList();

        if (submitted.Count > MaxLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lines),
                submitted.Count,
                $"A basket may carry at most {MaxLines} lines.");
        }

        var items = new List<BasketItem>(submitted.Count);

        foreach (var line in submitted)
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

            // And an unbounded one prices a basket nobody could ever be sent.
            if (line.Quantity > MaxQuantityPerLine)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lines),
                    line.Quantity,
                    $"Quantity for pizza {line.PizzaId} must be at most {MaxQuantityPerLine}.");
            }

            var unitPrice = await menu.GetUnitPriceAsync(line.PizzaId, cancellationToken)
                            ?? throw new UnknownPizzaException(line.PizzaId);

            items.Add(new BasketItem(line.PizzaId, line.Quantity, unitPrice));
        }

        return new Basket(items);
    }
}
