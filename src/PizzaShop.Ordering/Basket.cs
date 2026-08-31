namespace PizzaShop.Ordering;

/// <summary>
/// One line in the basket. Unit price comes from the server's own price table —
/// it is never accepted from the client.
/// </summary>
public sealed record BasketItem(int PizzaId, int Quantity, decimal UnitPrice);

/// <summary>
/// The full basket submitted by the client, enriched with server-side prices.
/// </summary>
public sealed record Basket(IReadOnlyList<BasketItem> Items)
{
    public decimal Subtotal => Items.Sum(i => i.UnitPrice * i.Quantity);
}
