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

    private static IResult ValidateCoupon(
        CouponValidationRequest request,
        IMenu menu,
        ICouponEvaluator evaluator,
        ILogger<CouponValidationRequest> logger)
    {
        Basket basket;
        try
        {
            basket = Basket.FromLines(
                request.Items.Select(i => new BasketLine(i.PizzaId, i.Quantity)),
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
            return Results.Problem(title: "Invalid quantity", detail: ex.Message, statusCode: 400);
        }

        var pricing = new PricingService(evaluator).Price(basket, request.CouponCode, DateTimeOffset.UtcNow);

        // Rule 9: named properties, never interpolation.
        logger.LogInformation(
            "Coupon validation: code={CouponCode} valid={IsValid} discount={Discount} reason={Reason}",
            request.CouponCode, pricing.CouponApplied, pricing.DiscountAmount, pricing.RejectionReason);

        return Results.Ok(new CouponValidationResponse(
            CouponCode: request.CouponCode,
            IsValid: pricing.CouponApplied,
            RejectionReason: pricing.RejectionReason?.ToString(),
            Subtotal: pricing.Subtotal,
            DiscountAmount: pricing.DiscountAmount,
            Total: pricing.Total,
            Description: pricing.CouponDescription));
    }
}

public sealed record CouponValidationRequest(
    string? CouponCode,
    IReadOnlyList<BasketLineRequest> Items);

public sealed record BasketLineRequest(int PizzaId, int Quantity);

public sealed record CouponValidationResponse(
    string? CouponCode,
    bool IsValid,
    string? RejectionReason,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    string? Description);
