using Microsoft.Extensions.Logging;
using PizzaShop.Coupons;
using PizzaShop.Infrastructure.Adapters;
using PizzaShop.Ordering;

namespace PizzaShop.Api.Endpoints;

public static class CouponEndpoints
{
    public static IEndpointRouteBuilder MapCouponEndpoints(this IEndpointRouteBuilder app)
    {
        // Rule 2: preview is read-only, no side effects.
        app.MapPost("/api/v1/coupons/validate", ValidateCoupon)
           .WithName("ValidateCoupon")
           .Produces<CouponValidationResponse>(200);

        return app;
    }

    private static async Task<IResult> ValidateCoupon(
        CouponValidationRequest request,
        IMenu menu,
        ICouponEvaluator evaluator,
        ILogger<CouponValidationRequest> logger)
    {
        // A malformed request is a 400, never a 500. See BasketRequestValidation.
        if (BasketRequestValidation.Validate(request.Items) is { } invalidBasket)
        {
            return invalidBasket;
        }

        Basket basket;
        try
        {
            basket = await Basket.FromLinesAsync(
                request.Items!.Select(i => new BasketLine(i.PizzaId, i.Quantity)),
                menu);
        }
        catch (UnknownPizzaException ex)
        {
            logger.LogWarning("Coupon validation rejected: unknown pizza {PizzaId}", ex.PizzaId);
            return Results.Problem(
                title: "Pizza not found",
                detail: ex.Message,
                statusCode: 400);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Covers both bounds Basket.FromLinesAsync enforces: the quantity on a line
            // and the number of lines. ex.Message names which, so the caller is told
            // what to change rather than just that something was wrong.
            return Results.Problem(title: "Invalid basket", detail: ex.Message, statusCode: 400);
        }

        var pricing = await new PricingService(evaluator)
            .PriceAsync(basket, request.CouponCode, DateTimeOffset.UtcNow);

        // Rule 3: a rejection always carries a reason. Asking "is this coupon valid?"
        // with no code is answered by NotFound, not by an invalid result with a null
        // reason, which would leave the caller with nothing to show the customer.
        var rejectionReason = string.IsNullOrWhiteSpace(request.CouponCode)
            ? CouponRejectionReason.NotFound
            : pricing.RejectionReason;

        // Rule 9: named properties, never interpolation.
        logger.LogInformation(
            "Coupon validation: code={CouponCode} valid={IsValid} discount={Discount} reason={Reason}",
            request.CouponCode, pricing.CouponApplied, pricing.DiscountAmount, rejectionReason);

        return Results.Ok(new CouponValidationResponse(
            CouponCode: request.CouponCode,
            IsValid: pricing.CouponApplied,
            RejectionReason: rejectionReason?.ToString(),
            Subtotal: pricing.Subtotal,
            DiscountAmount: pricing.DiscountAmount,
            Total: pricing.Total,
            Description: pricing.CouponDescription));
    }
}

public sealed record CouponValidationRequest(
    string? CouponCode,
    IReadOnlyList<BasketLineRequest>? Items);

public sealed record BasketLineRequest(int PizzaId, int Quantity);

public sealed record CouponValidationResponse(
    string? CouponCode,
    bool IsValid,
    string? RejectionReason,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    string? Description);
