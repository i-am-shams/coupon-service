using PizzaShop.Coupons;

namespace PizzaShop.Infrastructure.Repositories;

public interface ICouponRepository
{
    /// <summary>
    /// Returns all coupons as domain records. Used by the evaluator to check rules.
    /// </summary>
    IReadOnlyList<CouponRecord> GetAll();

    /// <summary>
    /// Attempts the atomic redemption:
    ///   UPDATE Coupons SET UsageCount = UsageCount + 1
    ///   WHERE Code = @code AND UsageCount &lt; RedemptionLimit
    /// Returns true if a row was updated (coupon was available), false if not (exhausted).
    /// </summary>
    Task<bool> TryRedeemAsync(string code, CancellationToken cancellationToken = default);
}
