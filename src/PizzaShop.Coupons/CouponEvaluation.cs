namespace PizzaShop.Coupons;

public sealed record CouponEvaluation(
    bool IsValid,
    decimal DiscountAmount,
    string? Description,
    CouponRejectionReason? Reason);
