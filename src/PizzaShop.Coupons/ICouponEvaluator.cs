namespace PizzaShop.Coupons;

/// <summary>
/// Evaluates a coupon code against a basket at a given point in time.
/// Time is a parameter so expiry rules are fully testable without clock mocking.
/// </summary>
public interface ICouponEvaluator
{
    CouponEvaluation Evaluate(string code, CouponBasket basket, DateTimeOffset asOf);
}
