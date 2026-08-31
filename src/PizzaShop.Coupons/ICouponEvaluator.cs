namespace PizzaShop.Coupons;

/// <summary>
/// Evaluates a coupon code against a basket at a given point in time.
/// Time is a parameter so expiry rules are fully testable without clock mocking.
/// </summary>
/// <remarks>
/// Asynchronous because an implementation may reach a database — the phase B
/// implementation does. The rules themselves are pure; the I/O is in fetching the
/// catalogue they run against.
/// </remarks>
public interface ICouponEvaluator
{
    Task<CouponEvaluation> EvaluateAsync(
        string code,
        CouponBasket basket,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default);
}
