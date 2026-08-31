namespace PizzaShop.Infrastructure.Entities;

public sealed class OrderEntity
{
    public int Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal Total { get; set; }
    public string? CouponCode { get; set; }
    public bool CouponApplied { get; set; }

    public ICollection<OrderLineEntity> Lines { get; set; } = new List<OrderLineEntity>();
}
