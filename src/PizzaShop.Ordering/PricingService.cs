using PizzaShop.Coupons;

namespace PizzaShop.Ordering;

/// <summary>
/// Calculates the price of an order. Ordering depends on ICouponEvaluator only —
/// it does not know how discounts are computed.
/// </summary>
public sealed class PricingService
{
    private readonly ICouponEvaluator _couponEvaluator;

    public PricingService(ICouponEvaluator couponEvaluator)
    {
        _couponEvaluator = couponEvaluator;
    }

    /// <summary>
    /// Price a basket, optionally applying a coupon code.
    /// <paramref name="asOf"/> is injected so callers control the point in time —
    /// no DateTime.UtcNow inside domain logic.
    /// </summary>
    public OrderPricing Price(Basket basket, string? couponCode, DateTimeOffset asOf)
    {
        var subtotal = basket.Subtotal;

        if (string.IsNullOrWhiteSpace(couponCode))
        {
            return new OrderPricing(
                Subtotal: subtotal,
                DiscountAmount: 0m,
                Total: subtotal,
                CouponApplied: false,
                CouponDescription: null,
                RejectionReason: null);
        }

        var evaluation = _couponEvaluator.Evaluate(couponCode, new CouponBasket(subtotal), asOf);

        // Total floors at zero — discount can never exceed the subtotal.
        var total = Math.Max(0m, subtotal - evaluation.DiscountAmount);

        return new OrderPricing(
            Subtotal: subtotal,
            DiscountAmount: evaluation.DiscountAmount,
            Total: total,
            CouponApplied: evaluation.IsValid,
            CouponDescription: evaluation.Description,
            RejectionReason: evaluation.Reason);
    }
}
