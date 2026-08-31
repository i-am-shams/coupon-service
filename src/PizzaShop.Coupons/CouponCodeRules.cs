namespace PizzaShop.Coupons;

/// <summary>
/// Facts about a coupon code that both the schema and the request validation must agree on.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because they did not agree, and the disagreement was a defect.
/// <c>OrderEntity.CouponCode</c> is <c>nvarchar(50)</c>; nothing validated the incoming
/// length. A code longer than that evaluated as <see cref="CouponRejectionReason.NotFound"/>,
/// was written onto the order anyway — a rejected coupon is still recorded — and
/// <c>SaveChangesAsync</c> threw when it met the column. The customer got a 500 for a request
/// that was merely malformed.
/// </para>
/// <para>
/// That is the same defect class as the absent <c>items</c> array, and it was missed for a
/// reason worth naming: <b>the limit lived in the schema and nowhere else</b>. Validation was
/// written by looking at the request, and 50 is not visible from there. Anyone adding a
/// constraint to a column has no reason to think about an endpoint, and anyone writing an
/// endpoint guard has no reason to open the EF configuration.
/// </para>
/// <para>
/// So the number now has one home and both sides reference it. Changing it changes the column
/// and the guard together, which is the only arrangement that cannot drift.
/// </para>
/// </remarks>
public static class CouponCodeRules
{
    /// <summary>
    /// Maximum length of a coupon code, in characters.
    /// </summary>
    /// <remarks>
    /// Referenced by the EF configurations for <c>Coupons.Code</c>, <c>Orders.CouponCode</c>
    /// and <c>CouponRedemptions.CouponCode</c>, and by the API request validation. The value
    /// is unchanged from the original schema, so it needs no migration.
    /// </remarks>
    public const int MaxLength = 50;
}
