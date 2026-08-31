namespace PizzaShop.Coupons;

/// <summary>
/// A basket that the coupon evaluator receives — just the subtotal, because
/// coupons do not need to know what pizzas are in it.
/// </summary>
public sealed record CouponBasket(decimal Subtotal);
