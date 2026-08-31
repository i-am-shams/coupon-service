namespace PizzaShop.Coupons;

/// <summary>
/// Evaluates coupon codes against a supplied catalogue.
/// The catalogue is injected so this class is usable in tests without a database.
/// Phase B will supply the catalogue from EF Core via an adapter.
/// </summary>
public sealed class CouponEvaluator : ICouponEvaluator
{
    private readonly IReadOnlyDictionary<string, CouponRecord> _catalogue;

    public CouponEvaluator(IEnumerable<CouponRecord> catalogue)
    {
        _catalogue = catalogue.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The catalogue is already in memory here, so there is nothing to await — the
    /// rules are pure. The async signature belongs to the port, not to this class.
    /// </summary>
    public Task<CouponEvaluation> EvaluateAsync(
        string code,
        CouponBasket basket,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(code, basket, asOf));

    public CouponEvaluation Evaluate(string code, CouponBasket basket, DateTimeOffset asOf)
    {
        // A missing or blank code is not an error — it is simply not a coupon.
        // The interface is public and phase D calls it straight from an endpoint.
        if (string.IsNullOrWhiteSpace(code) || !_catalogue.TryGetValue(code, out var coupon))
            return Rejected(CouponRejectionReason.NotFound);

        // The boundary is inclusive: a coupon is still valid at the instant it
        // expires, and invalid from the first tick after.
        if (asOf > coupon.ExpiresAt)
            return Rejected(CouponRejectionReason.Expired);

        if (basket.Subtotal < coupon.MinimumOrderValue)
            return Rejected(CouponRejectionReason.MinimumSpendNotMet);

        if (coupon.UsageCount >= coupon.RedemptionLimit)
            return Rejected(CouponRejectionReason.RedemptionLimitReached);

        var discount = CalculateDiscount(coupon, basket.Subtotal);

        return new CouponEvaluation(
            IsValid: true,
            DiscountAmount: discount,
            Description: coupon.Description,
            Reason: null);
    }

    private static decimal CalculateDiscount(CouponRecord coupon, decimal subtotal)
    {
        var raw = coupon.Type switch
        {
            CouponType.Percentage => subtotal * coupon.Value / 100m,
            CouponType.FixedAmount => coupon.Value,
            _ => throw new InvalidOperationException($"Unknown coupon type {coupon.Type}"),
        };

        // Rounding happens once, on the final discount — not per step.
        // Discount is capped at the subtotal so the total never goes below zero.
        var rounded = Math.Round(raw, 2, MidpointRounding.AwayFromZero);
        return Math.Min(rounded, subtotal);
    }

    private static CouponEvaluation Rejected(CouponRejectionReason reason) =>
        new(IsValid: false, DiscountAmount: 0m, Description: null, Reason: reason);
}
