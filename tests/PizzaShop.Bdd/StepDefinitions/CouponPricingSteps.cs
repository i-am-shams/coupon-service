using PizzaShop.Coupons;
using PizzaShop.Ordering;
using Reqnroll;

namespace PizzaShop.Bdd.StepDefinitions;

/// <summary>
/// Shared context for coupon pricing scenarios, held in Reqnroll's ScenarioContext.
/// Each scenario gets a fresh instance.
/// </summary>
[Binding]
public sealed class CouponPricingSteps
{
    // Fixed "now" so expiry is deterministic.
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PastDate = Now.AddDays(-1);
    private static readonly DateTimeOffset FutureDate = Now.AddYears(1);

    // Pizza price table, populated by the Background step.
    private readonly Dictionary<int, decimal> _prices = new();

    // Coupon catalogue, populated by Given steps.
    private readonly List<CouponRecord> _coupons = new();

    // Basket items (pizzaId, quantity) — no price from the client.
    private readonly List<(int PizzaId, int Quantity)> _basketItems = new();

    // Result of the last Price() call.
    private OrderPricing? _result;

    // ── Background ─────────────────────────────────────────────────────────

    [Given("the menu has the following pizzas:")]
    public void GivenMenuPizzas(DataTable table)
    {
        foreach (var row in table.Rows)
        {
            var id = int.Parse(row["PizzaId"]);
            var price = decimal.Parse(row["UnitPrice"]);
            _prices[id] = price;
        }
    }

    // ── Basket ──────────────────────────────────────────────────────────────

    [Given("I have a basket with:")]
    public void GivenBasket(DataTable table)
    {
        _basketItems.Clear();
        foreach (var row in table.Rows)
            _basketItems.Add((int.Parse(row["PizzaId"]), int.Parse(row["Quantity"])));
    }

    // ── Coupon catalogue builders ────────────────────────────────────────────

    [Given(@"a (\d+(?:\.\d+)?)% off coupon ""([^""]*)"" with no minimum spend and a limit of (\d+) uses, expiring in the future")]
    public void GivenPercentageCouponNoMinFuture(decimal pct, string code, int limit)
    {
        _coupons.Add(new CouponRecord(
            Code: code,
            Type: CouponType.Percentage,
            Value: pct,
            Description: $"{pct}% off your order",
            ExpiresAt: FutureDate,
            MinimumOrderValue: 0m,
            RedemptionLimit: limit,
            UsageCount: 0));
    }

    [Given(@"a (\d+(?:\.\d+)?) fixed-amount coupon ""([^""]*)"" with no minimum spend and a limit of (\d+) uses, expiring in the future")]
    public void GivenFixedCouponNoMinFuture(decimal amount, string code, int limit)
    {
        _coupons.Add(new CouponRecord(
            Code: code,
            Type: CouponType.FixedAmount,
            Value: amount,
            Description: $"€{amount} off your order",
            ExpiresAt: FutureDate,
            MinimumOrderValue: 0m,
            RedemptionLimit: limit,
            UsageCount: 0));
    }

    [Given(@"a (\d+(?:\.\d+)?)% off coupon ""([^""]*)"" with no minimum spend and a limit of (\d+) uses, expiring in the past")]
    public void GivenPercentageCouponNoMinPast(decimal pct, string code, int limit)
    {
        _coupons.Add(new CouponRecord(
            Code: code,
            Type: CouponType.Percentage,
            Value: pct,
            Description: $"{pct}% off your order",
            ExpiresAt: PastDate,
            MinimumOrderValue: 0m,
            RedemptionLimit: limit,
            UsageCount: 0));
    }

    [Given(@"a (\d+(?:\.\d+)?)% off coupon ""([^""]*)"" requiring a minimum spend of (\d+(?:\.\d+)?) and a limit of (\d+) uses, expiring in the future")]
    public void GivenPercentageCouponWithMinFuture(decimal pct, string code, decimal minSpend, int limit)
    {
        _coupons.Add(new CouponRecord(
            Code: code,
            Type: CouponType.Percentage,
            Value: pct,
            Description: $"{pct}% off your order",
            ExpiresAt: FutureDate,
            MinimumOrderValue: minSpend,
            RedemptionLimit: limit,
            UsageCount: 0));
    }

    [Given(@"a (\d+(?:\.\d+)?)% off coupon ""([^""]*)"" with no minimum spend, already used (\d+) times out of a limit of (\d+), expiring in the future")]
    public void GivenPercentageCouponMaxed(decimal pct, string code, int usageCount, int limit)
    {
        _coupons.Add(new CouponRecord(
            Code: code,
            Type: CouponType.Percentage,
            Value: pct,
            Description: $"{pct}% off your order",
            ExpiresAt: FutureDate,
            MinimumOrderValue: 0m,
            RedemptionLimit: limit,
            UsageCount: usageCount));
    }

    // ── Pricing actions ──────────────────────────────────────────────────────

    [When("I price the basket without a coupon")]
    public void WhenPriceNoCoupon() => _result = BuildPricingService().Price(BuildBasket(), null, Now);

    [When(@"I price the basket with coupon ""([^""]*)""")]
    public void WhenPriceWithCoupon(string code) => _result = BuildPricingService().Price(BuildBasket(), code, Now);

    // ── Assertions ───────────────────────────────────────────────────────────

    [Then(@"the subtotal should be (\d+(?:\.\d+)?)")]
    public void ThenSubtotal(decimal expected) =>
        Assert.Equal(expected, _result!.Subtotal);

    [Then(@"the discount should be (\d+(?:\.\d+)?)")]
    public void ThenDiscount(decimal expected) =>
        Assert.Equal(expected, _result!.DiscountAmount);

    [Then(@"the total should be (\d+(?:\.\d+)?)")]
    public void ThenTotal(decimal expected) =>
        Assert.Equal(expected, _result!.Total);

    [Then("the total should equal the subtotal")]
    public void ThenTotalEqualsSubtotal() =>
        Assert.Equal(_result!.Subtotal, _result!.Total);

    [Then(@"the coupon should be rejected with reason ""([^""]*)""")]
    public void ThenRejectedWithReason(string reason)
    {
        Assert.False(_result!.CouponApplied);
        // The mapping from readable step text to the enum lives here, not in the feature file.
        var expected = Enum.Parse<CouponRejectionReason>(reason);
        Assert.Equal(expected.ToString(), _result!.RejectionReason);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Basket BuildBasket()
    {
        var items = _basketItems
            .Select(i => new BasketItem(i.PizzaId, i.Quantity, _prices[i.PizzaId]))
            .ToList();
        return new Basket(items);
    }

    private PricingService BuildPricingService() =>
        new PricingService(new CouponEvaluator(_coupons));
}
