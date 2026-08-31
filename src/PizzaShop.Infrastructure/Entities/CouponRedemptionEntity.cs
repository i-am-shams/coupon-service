namespace PizzaShop.Infrastructure.Entities;

public sealed class CouponRedemptionEntity
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string CouponCode { get; set; } = string.Empty;
    public DateTimeOffset RedeemedAt { get; set; }

    public OrderEntity Order { get; set; } = null!;
}
