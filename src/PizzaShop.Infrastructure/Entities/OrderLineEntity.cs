namespace PizzaShop.Infrastructure.Entities;

public sealed class OrderLineEntity
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int PizzaId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    public OrderEntity Order { get; set; } = null!;
}
