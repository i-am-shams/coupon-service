namespace PizzaShop.Api.Endpoints;

/// <summary>
/// The one place both basket-carrying endpoints check that a request actually carries a
/// basket.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a defect found by probing the deployed API rather than by
/// reading the code. <c>POST /coupons/validate</c> with the body
/// <c>{"couponCode":"PIZZA10"}</c> — no <c>items</c> property at all — returned
/// <b>HTTP 500</b>.
/// </para>
/// <para>
/// The cause is that a non-nullable reference type in a record is a compile-time
/// annotation and nothing more. <c>System.Text.Json</c> does not enforce it, so an
/// absent property binds as null, <c>request.Items.Select(...)</c> throws a
/// <see cref="NullReferenceException"/>, and the exception handler turns a malformed
/// request into a server fault.
/// </para>
/// <para>
/// That contradicts the contract every deliverable document states: 4xx is for
/// malformed requests, 5xx is for real faults. A 500 here tells the caller the server
/// broke when in fact the request did. The DTOs now declare <c>Items</c> as nullable so
/// the compiler forces this check rather than leaving it to review.
/// </para>
/// </remarks>
internal static class BasketRequestValidation
{
    /// <summary>
    /// Returns a 400 <c>ProblemDetails</c> result when the request carries no usable
    /// basket, or <see langword="null"/> when it does and the caller may proceed.
    /// </summary>
    public static IResult? Validate(IReadOnlyList<BasketLineRequest>? items)
    {
        if (items is null)
        {
            return Results.Problem(
                title: "Missing basket",
                detail: "The request must include an 'items' array of { pizzaId, quantity }.",
                statusCode: 400);
        }

        // An empty array is well-formed JSON and a meaningless order. Answering it with a
        // zero-priced 201 would create an order for nothing; answering a coupon preview
        // with a zero subtotal would report a discount against a basket that does not
        // exist. Both are worse than saying so.
        if (items.Count == 0)
        {
            return Results.Problem(
                title: "Empty basket",
                detail: "The 'items' array must contain at least one line.",
                statusCode: 400);
        }

        return null;
    }
}
