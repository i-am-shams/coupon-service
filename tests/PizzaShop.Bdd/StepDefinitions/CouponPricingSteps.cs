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

    // Set when a scenario expects basket construction to be refused outright.
    private Exception? _refusal;

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
    public async Task WhenPriceNoCoupon() =>
        _result = await BuildPricingService().PriceAsync(await BuildBasketAsync(), null, Now);

    [When(@"I price the basket with coupon ""([^""]*)""")]
    public async Task WhenPriceWithCoupon(string code) =>
        _result = await BuildPricingService().PriceAsync(await BuildBasketAsync(), code, Now);

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
        Assert.Equal(expected, _result!.RejectionReason);
    }

    // ── Basket bounds ────────────────────────────────────────────────────────

    [When("I price the basket without a coupon expecting it to be refused")]
    public async Task WhenPricingIsRefused()
    {
        try
        {
            await BuildBasketAsync();
        }
        catch (Exception ex)
        {
            // Captured rather than allowed to fail the scenario: refusing is the
            // behaviour under test, so the Then step decides whether the right thing
            // was refused for the right reason.
            _refusal = ex;
        }
    }

    [Then("pricing should be refused because the quantity is out of range")]
    public void ThenRefusedForQuantity()
    {
        Assert.NotNull(_refusal);
        var ex = Assert.IsType<InvalidBasketException>(_refusal);

        // Asserting on the bound rather than a fixed number, so raising the cap does
        // not silently turn this into a test of nothing.
        Assert.Contains($"at most {Basket.MaxQuantityPerLine}", ex.CustomerFacingMessage);

        // The message the customer is shown must not carry the exception type's own
        // parameter and actual-value suffix. That suffix reached the browser verbatim
        // once — "(Parameter 'lines') Actual value was 52." — and this is what stops it
        // coming back if someone reverts the endpoints to ex.Message.
        Assert.DoesNotContain("Parameter", ex.CustomerFacingMessage);
        Assert.DoesNotContain("Actual value", ex.CustomerFacingMessage);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // The scenarios build a basket the only way production code can: client lines
    // plus the server's menu. There is no path here that supplies a price directly.
    private Task<Basket> BuildBasketAsync() =>
        Basket.FromLinesAsync(
            _basketItems.Select(i => new BasketLine(i.PizzaId, i.Quantity)),
            new MenuPriceTable(_prices));

    private PricingService BuildPricingService() =>
        new PricingService(new CouponEvaluator(_coupons));

    /// <summary>The server's price data for a scenario, standing in for phase B's menu.</summary>
    private sealed class MenuPriceTable(IReadOnlyDictionary<int, decimal> prices) : IMenu
    {
        public Task<decimal?> GetUnitPriceAsync(int pizzaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(prices.TryGetValue(pizzaId, out var price) ? price : (decimal?)null);
    }
}
