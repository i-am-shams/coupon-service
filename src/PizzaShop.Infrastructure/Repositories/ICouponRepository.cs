using PizzaShop.Coupons;

namespace PizzaShop.Infrastructure.Repositories;

public interface ICouponRepository
{
    /// <summary>
    /// Returns all coupons as domain records. Used by the evaluator to check rules.
    /// </summary>
    Task<IReadOnlyList<CouponRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts the atomic redemption:
    ///   UPDATE Coupons SET UsageCount = UsageCount + 1
    ///   WHERE Code = @code AND UsageCount &lt; RedemptionLimit
    /// Returns true if a row was updated (coupon was available), false if not (exhausted).
    /// </summary>
    /// <remarks>
    /// This runs as its own statement and commits immediately unless the caller has
    /// opened a transaction on the same <see cref="PizzaShopDbContext"/>. The order
    /// endpoint opens one, so the redemption and the order it belongs to succeed or
    /// fail together.
    /// </remarks>
    Task<bool> TryRedeemAsync(string code, CancellationToken cancellationToken = default);
}
