using PizzaShop.Coupons;

namespace PizzaShop.Ordering;

/// <summary>
/// Result of pricing a basket, with or without a coupon.
/// </summary>
/// <remarks>
/// <see cref="RejectionReason"/> stays the enum rather than a string: rule 3 exists
/// so one value drives the customer-facing message, the log entry and the test
/// assertion. Flattening it to text here would move that decision into whichever
/// caller formats it first. Serialisation to a string belongs in the API DTO.
/// </remarks>
public sealed record OrderPricing(
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    bool CouponApplied,
    string? CouponDescription,
    CouponRejectionReason? RejectionReason);
