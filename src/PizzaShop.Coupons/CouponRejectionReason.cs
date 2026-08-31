namespace PizzaShop.Coupons;

public enum CouponRejectionReason
{
    NotFound,
    Expired,
    MinimumSpendNotMet,
    RedemptionLimitReached,
}
