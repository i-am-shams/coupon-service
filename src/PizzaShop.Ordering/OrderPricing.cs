namespace PizzaShop.Ordering;

/// <summary>
/// Result of pricing a basket, with or without a coupon.
/// </summary>
public sealed record OrderPricing(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    bool CouponApplied,
    string? CouponDescription,
    string? RejectionReason);
