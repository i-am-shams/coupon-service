namespace PizzaShop.Infrastructure.Entities;

public enum CouponTypeEntity
{
    Percentage,
    FixedAmount,
}

public sealed class CouponEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public CouponTypeEntity Type { get; set; }
    public decimal Value { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public decimal MinimumOrderValue { get; set; }
    public int RedemptionLimit { get; set; }
    public int UsageCount { get; set; }
}
