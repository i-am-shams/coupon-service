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
/// Thrown when a basket breaks one of its own bounds — a line quantity outside
/// 1..<see cref="Basket.MaxQuantityPerLine"/>, or more than <see cref="Basket.MaxLines"/>
/// lines.
/// </summary>
/// <remarks>
/// <para>
/// It derives from <see cref="ArgumentOutOfRangeException"/>, so every existing caller
/// that catches that type keeps working and the type still says the right thing about
/// what went wrong.
/// </para>
/// <para>
/// It exists because <see cref="Exception.Message"/> on the base type is not safe to show
/// a customer. .NET composes it as <c>{message} (Parameter '{paramName}')</c> followed by
/// <c>Actual value was {actualValue}.</c>, and the endpoints put that string straight into
/// <c>ProblemDetails.detail</c> — so a customer who clicked the + button past fifty was
/// shown <c>"Quantity for pizza 1 must be at most 50. (Parameter 'lines') Actual value was
/// 52."</c>. The first sentence is for them; the rest is an implementation detail of the
/// exception type, and naming a parameter of a method they have never heard of is the
/// kind of thing that makes an application look like it is leaking its insides.
/// </para>
/// <para>
/// <see cref="CustomerFacingMessage"/> is that first sentence on its own. The base
/// <c>Message</c> is left exactly as it was, because a log line and a stack trace both
/// want the parameter name.
/// </para>
/// </remarks>
public sealed class InvalidBasketException(string message, string paramName, object actualValue)
    : ArgumentOutOfRangeException(paramName, actualValue, message)
{
    /// <summary>The message without .NET's parameter and actual-value suffix.</summary>
    public string CustomerFacingMessage { get; } = message;
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
    /// <exception cref="InvalidBasketException">A line has a quantity outside 1..<see cref="MaxQuantityPerLine"/>, or there are more than <see cref="MaxLines"/> lines.</exception>
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
            throw new InvalidBasketException(
                $"A basket may carry at most {MaxLines} lines.",
                nameof(lines),
                submitted.Count);
        }

        var items = new List<BasketItem>(submitted.Count);

        foreach (var line in submitted)
        {
            // A negative or zero quantity would subtract from the subtotal, which is
            // the same class of problem as accepting a price from the client.
            if (line.Quantity < 1)
            {
                throw new InvalidBasketException(
                    $"Quantity for pizza {line.PizzaId} must be at least 1.",
                    nameof(lines),
                    line.Quantity);
            }

            // And an unbounded one prices a basket nobody could ever be sent.
            if (line.Quantity > MaxQuantityPerLine)
            {
                throw new InvalidBasketException(
                    $"Quantity for pizza {line.PizzaId} must be at most {MaxQuantityPerLine}.",
                    nameof(lines),
                    line.Quantity);
            }

            var unitPrice = await menu.GetUnitPriceAsync(line.PizzaId, cancellationToken)
                            ?? throw new UnknownPizzaException(line.PizzaId);

            items.Add(new BasketItem(line.PizzaId, line.Quantity, unitPrice));
        }

        return new Basket(items);
    }
}
