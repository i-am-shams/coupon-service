namespace PizzaShop.Coupons;

public enum CouponType
{
    Percentage,
    FixedAmount,
}

/// <summary>
/// A coupon as stored in the catalogue. Phase B will map this to an EF entity.
/// </summary>
public sealed record CouponRecord(
    string Code,
    CouponType Type,
    decimal Value,
    string Description,
    DateTimeOffset ExpiresAt,
    decimal MinimumOrderValue,
    int RedemptionLimit,
    int UsageCount);
