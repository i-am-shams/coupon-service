using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PizzaShop.Coupons;
using PizzaShop.Infrastructure;
using PizzaShop.Infrastructure.Entities;
using PizzaShop.Infrastructure.Repositories;
using PizzaShop.Ordering;

namespace PizzaShop.Api.Endpoints;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/orders", PlaceOrder)
           .WithName("PlaceOrder")
           .Produces<OrderResponse>(201);

        return app;
    }

    private static async Task<IResult> PlaceOrder(
        OrderRequest request,
        IMenu menu,
        ICouponEvaluator evaluator,
        ICouponRepository couponRepository,
        PizzaShopDbContext db,
        ILogger<OrderRequest> logger)
    {
        // A malformed request is a 400, never a 500. See BasketRequestValidation.
        if (BasketRequestValidation.Validate(request.CouponCode, request.Items) is { } invalidBasket)
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
            logger.LogWarning("Order rejected: unknown pizza {PizzaId}", ex.PizzaId);
            return Results.Problem(title: "Pizza not found", detail: ex.Message, statusCode: 400);
        }
        catch (InvalidBasketException ex)
        {
            // Covers both bounds Basket.FromLinesAsync enforces: the quantity on a line
            // and the number of lines. CustomerFacingMessage names which, so the caller is
            // told what to change rather than just that something was wrong.
            //
            // CustomerFacingMessage and not ex.Message: the base ArgumentOutOfRangeException
            // appends "(Parameter 'lines')" and "Actual value was 52." to whatever message
            // it is given, and this string is what the browser displays.
            return Results.Problem(title: "Invalid basket", detail: ex.CustomerFacingMessage, statusCode: 400);
        }

        var asOf = DateTimeOffset.UtcNow;
        var pricingService = new PricingService(evaluator);

        // Retries are enabled on the connection (Program.cs), which means
        // CreateExecutionStrategy returns a retrying strategy — and a retrying strategy
        // refuses a user-initiated transaction unless the transaction is opened inside
        // its ExecuteAsync. Without this wrapper every order carrying a coupon throws
        // "The configured execution strategy ... does not support user-initiated
        // transactions", which reads as an EF configuration fault rather than as the
        // documented consequence of turning retries on.
        //
        // Everything the transaction depends on is computed INSIDE the block. A retry
        // re-runs the whole thing against a transaction that was rolled back, so a
        // redemption decision taken on a previous attempt must not survive into the next
        // one — otherwise a retried order could be priced as though it held a redemption
        // it no longer has.
        var strategy = db.Database.CreateExecutionStrategy();

        var outcome = await strategy.ExecuteAsync(async () =>
        {
            // A retry starts from a change tracker still holding the entities the failed
            // attempt added. Clearing it is what makes the block genuinely repeatable.
            db.ChangeTracker.Clear();

            var pricing = await pricingService.PriceAsync(basket, request.CouponCode, asOf);
            var couponApplied = false;
            var rejectionReason = pricing.RejectionReason;

            // The redemption and the order are one operation. ExecuteUpdateAsync commits
            // immediately on its own, so without this transaction a failure in the order
            // write would leave a coupon consumed against an order that does not exist.
            await using var transaction = await db.Database.BeginTransactionAsync();

            if (pricing.CouponApplied && !string.IsNullOrWhiteSpace(request.CouponCode))
            {
                // Rule 4: atomic redemption — single UPDATE, no read before write.
                couponApplied = await couponRepository.TryRedeemAsync(request.CouponCode);

                if (!couponApplied)
                {
                    // Coupon was exhausted between the evaluation and the redemption attempt.
                    // Re-price without the coupon.
                    logger.LogWarning(
                        "Coupon {CouponCode} exhausted between evaluation and redemption; order repriced without it",
                        request.CouponCode);
                    pricing = await pricingService.PriceAsync(basket, null, asOf);
                    rejectionReason = CouponRejectionReason.RedemptionLimitReached;
                }
            }

            var order = new OrderEntity
            {
                CreatedAt      = asOf,
                Subtotal       = pricing.Subtotal,
                DiscountAmount = pricing.DiscountAmount,
                Total          = pricing.Total,
                CouponCode     = request.CouponCode,
                CouponApplied  = couponApplied,
                Lines          = basket.Items.Select(i => new OrderLineEntity
                {
                    PizzaId   = i.PizzaId,
                    Quantity  = i.Quantity,
                    UnitPrice = i.UnitPrice,
                }).ToList(),
            };

            db.Orders.Add(order);
            await db.SaveChangesAsync();

            if (couponApplied)
            {
                // The audit trail the Coupons table's UsageCount cannot give: which order
                // consumed which redemption, and when. Written inside the same transaction.
                db.CouponRedemptions.Add(new CouponRedemptionEntity
                {
                    OrderId    = order.Id,
                    CouponCode = request.CouponCode!,
                    RedeemedAt = asOf,
                });
                await db.SaveChangesAsync();
            }

            await transaction.CommitAsync();

            return (Order: order, CouponApplied: couponApplied, RejectionReason: rejectionReason);
        });

        var order = outcome.Order;
        var couponApplied = outcome.CouponApplied;
        var rejectionReason = outcome.RejectionReason;

        // Rule 9: named properties, never interpolation.
        logger.LogInformation(
            "Order {OrderId} placed: subtotal={Subtotal} discount={Discount} total={Total} coupon={CouponCode} applied={CouponApplied}",
            order.Id, order.Subtotal, order.DiscountAmount, order.Total,
            request.CouponCode, couponApplied);

        return Results.Created(
            $"/api/v1/orders/{order.Id}",
            new OrderResponse(
                OrderId: order.Id,
                Subtotal: order.Subtotal,
                DiscountAmount: order.DiscountAmount,
                Total: order.Total,
                CouponApplied: couponApplied,
                CouponCode: couponApplied ? request.CouponCode : null,
                RejectionReason: rejectionReason?.ToString()));
    }
}

public sealed record OrderRequest(
    string? CouponCode,
    IReadOnlyList<BasketLineRequest>? Items);

public sealed record OrderResponse(
    int OrderId,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal Total,
    bool CouponApplied,
    string? CouponCode,
    string? RejectionReason);
