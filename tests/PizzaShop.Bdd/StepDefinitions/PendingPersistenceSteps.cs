using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PizzaShop.Api.Endpoints;
using PizzaShop.Infrastructure;
using PizzaShop.Infrastructure.Entities;
using Reqnroll;

namespace PizzaShop.Bdd.StepDefinitions;

/// <summary>
/// Step definitions for the two scenarios that require the HTTP layer and a real
/// database. Each scenario gets its own in-memory database via the factory.
/// </summary>
[Binding]
public sealed class PersistenceSteps : IDisposable
{
    private PizzaShopWebApplicationFactory? _factory;
    private HttpClient? _client;

    // Captured during Given steps; used in When/Then.
    private string? _couponCode;
    private CouponValidationResponse? _lastValidation;
    private OrderResponse? _orderResponse;

    // Held undeserialised, because the scenario asserts on the status code rather than
    // on a body it may not have.
    private HttpResponseMessage? _rawResponse;

    // ── Given ─────────────────────────────────────────────────────────────

    [Given(@"a coupon ""([^""]*)"" with a redemption limit of (\d+)")]
    public void GivenCouponWithRedemptionLimit(string code, int limit)
    {
        _couponCode = code;

        _factory = new PizzaShopWebApplicationFactory();
        _client = _factory.CreateClient(); // starts server, initialises DI
        _factory.SeedDatabase(
            pizzas: new[]
            {
                new PizzaEntity { Id = 1, Name = "Margherita", Description = "Test pizza", Price = 10.00m },
            },
            coupons: new[]
            {
                new CouponEntity
                {
                    Code              = code,
                    Type              = CouponTypeEntity.Percentage,
                    Value             = 10m,
                    Description       = "10% off",
                    ExpiresAt         = DateTimeOffset.UtcNow.AddYears(1),
                    MinimumOrderValue = 0m,
                    RedemptionLimit   = limit,
                    UsageCount        = 0,
                },
            });
    }

    // ── Ordering with a coupon ────────────────────────────────────────────
    //
    // These two Givens seed a coupon and a pizza together, because the scenarios they
    // serve exercise the whole order path and need both. The catalogue differs only in
    // the usage count: one coupon has redemptions left, the other does not.

    [Given(@"a (\d+)% coupon ""([^""]*)"" with a redemption limit of (\d+) and a pizza costing (\d+(?:\.\d+)?)")]
    public void GivenCouponWithHeadroomAndPizza(int percent, string code, int limit, decimal price) =>
        SeedCouponAndPizza(code, percent, limit, usageCount: 0, price);

    [Given(@"a (\d+)% coupon ""([^""]*)"" already used (\d+) times out of (\d+) and a pizza costing (\d+(?:\.\d+)?)")]
    public void GivenExhaustedCouponAndPizza(int percent, string code, int used, int limit, decimal price) =>
        SeedCouponAndPizza(code, percent, limit, usageCount: used, price);

    private void SeedCouponAndPizza(string code, int percent, int limit, int usageCount, decimal price)
    {
        _couponCode = code;

        _factory = new PizzaShopWebApplicationFactory();
        _client = _factory.CreateClient(); // starts server, initialises DI
        _factory.SeedDatabase(
            pizzas: new[]
            {
                new PizzaEntity { Id = 1, Name = "Margherita", Description = "Test pizza", Price = price },
            },
            coupons: new[]
            {
                new CouponEntity
                {
                    Code              = code,
                    Type              = CouponTypeEntity.Percentage,
                    Value             = percent,
                    Description       = $"{percent}% off",
                    ExpiresAt         = DateTimeOffset.UtcNow.AddYears(1),
                    MinimumOrderValue = 0m,
                    RedemptionLimit   = limit,
                    UsageCount        = usageCount,
                },
            });
    }

    [Given(@"I submit an order claiming a pizza costs (\d+(?:\.\d+)?) but the server has it at (\d+(?:\.\d+)?)")]
    public async Task GivenOrderWithFakePrice(decimal clientPrice, decimal serverPrice)
    {
        _factory = new PizzaShopWebApplicationFactory();
        _client = _factory.CreateClient(); // starts server, initialises DI
        _factory.SeedDatabase(
            pizzas: new[]
            {
                new PizzaEntity { Id = 1, Name = "Margherita", Description = "Test pizza", Price = serverPrice },
            });

        // Raw JSON, not the typed DTO. The client really does send a price here —
        // that is the whole point of the scenario. Going through OrderRequest would
        // make the claim unsendable, and the assertion would then pass because the
        // test could not express the attack rather than because the server resisted it.
        var json = $$"""
            {
              "couponCode": null,
              "items": [ { "pizzaId": 1, "quantity": 1, "unitPrice": {{clientPrice}}, "price": {{clientPrice}} } ]
            }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/v1/orders", content);
        response.EnsureSuccessStatusCode();
        _orderResponse = await response.Content.ReadFromJsonAsync<OrderResponse>();
    }

    // ── When ──────────────────────────────────────────────────────────────

    [When(@"I preview the coupon ""([^""]*)"" three times")]
    public async Task WhenPreviewThreeTimes(string code)
    {
        var request = new CouponValidationRequest(
            CouponCode: code,
            Items: new[] { new BasketLineRequest(PizzaId: 1, Quantity: 1) });

        for (var i = 0; i < 3; i++)
        {
            var response = await _client!.PostAsJsonAsync("/api/v1/coupons/validate", request);
            response.EnsureSuccessStatusCode();
            _lastValidation = await response.Content.ReadFromJsonAsync<CouponValidationResponse>();
        }
    }

    [When("I submit a coupon validation with no items property at all")]
    public async Task WhenValidationWithNoItems()
    {
        _factory = new PizzaShopWebApplicationFactory();
        _client = _factory.CreateClient();
        _factory.SeedDatabase(
            pizzas: new[]
            {
                new PizzaEntity { Id = 1, Name = "Margherita", Description = "Test pizza", Price = 10.00m },
            });

        // Raw JSON, because the typed DTO cannot express an *absent* property — and the
        // absence is the whole point. This is verbatim the body that returned HTTP 500
        // from the deployed gateway before BasketRequestValidation existed.
        using var content = new StringContent(
            """{"couponCode":"PIZZA10"}""", Encoding.UTF8, "application/json");

        _rawResponse = await _client.PostAsync("/api/v1/coupons/validate", content);
    }

    [When(@"I place an order for (\d+) of that pizza with coupon ""([^""]*)""")]
    public async Task WhenPlaceOrderWithCoupon(int quantity, string code)
    {
        var request = new OrderRequest(
            CouponCode: code,
            Items: new[] { new BasketLineRequest(PizzaId: 1, Quantity: quantity) });

        // Held raw as well as deserialised: the scenario asserts on the status code
        // (201, not 200) as well as on the body.
        _rawResponse = await _client!.PostAsJsonAsync("/api/v1/orders", request);

        if (_rawResponse.IsSuccessStatusCode)
        {
            _orderResponse = await _rawResponse.Content.ReadFromJsonAsync<OrderResponse>();
        }
    }

    [When(@"I submit an order with a coupon code of (\d+) characters")]
    public async Task WhenOrderWithOverlongCouponCode(int length)
    {
        _factory = new PizzaShopWebApplicationFactory();
        _client = _factory.CreateClient();
        _factory.SeedDatabase(
            pizzas: new[]
            {
                new PizzaEntity { Id = 1, Name = "Margherita", Description = "Test pizza", Price = 10.00m },
            });

        // Worth being explicit about what this scenario can and cannot prove.
        //
        // It does NOT reproduce the original crash. The suite runs on SQLite, which is
        // dynamically typed and does not enforce VARCHAR length at all, so the oversized
        // value would have been stored happily and the order would have returned 201.
        // The production failure needs SQL Server, which no test here uses.
        //
        // It still has teeth: before the guard existed this returned 201, and it now
        // returns 400. What it pins is the validation, which is the thing that has to
        // hold. The class of bug — a schema constraint the tests structurally cannot
        // see — is recorded in docs/decisions.md rather than papered over here.
        var code = new string('A', length);
        var json = $$"""
            { "couponCode": "{{code}}", "items": [ { "pizzaId": 1, "quantity": 1 } ] }
            """;

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        _rawResponse = await _client.PostAsync("/api/v1/orders", content);
    }

    // ── Then ──────────────────────────────────────────────────────────────

    [Then(@"the response status should be (\d+)")]
    public void ThenResponseStatusShouldBe(int expected)
    {
        Assert.NotNull(_rawResponse);
        Assert.Equal(expected, (int)_rawResponse!.StatusCode);
    }

    [Then("the response should not be a server error")]
    public void ThenResponseIsNotAServerError()
    {
        Assert.NotNull(_rawResponse);

        // Stated separately from the status assertion on purpose. The contract every
        // deliverable document makes is that 4xx means the request was wrong and 5xx
        // means the server was — so a malformed request surfacing as 5xx is a
        // documentation contradiction, not just an off-by-one status code.
        Assert.True(
            (int)_rawResponse!.StatusCode < 500,
            $"A malformed request must not be reported as a server fault. Got {(int)_rawResponse.StatusCode}.");
    }

    [Then("the coupon should still be valid on the fourth preview")]
    public async Task ThenStillValidOnFourthPreview()
    {
        // Rule 2: three previews must not have consumed any redemption.
        // A fourth preview must still see the coupon as valid.
        var request = new CouponValidationRequest(
            CouponCode: _couponCode,
            Items: new[] { new BasketLineRequest(PizzaId: 1, Quantity: 1) });

        var response = await _client!.PostAsJsonAsync("/api/v1/coupons/validate", request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CouponValidationResponse>();

        Assert.NotNull(result);
        Assert.True(result.IsValid, $"Expected coupon to be valid on 4th preview but got reason: {result.RejectionReason}");

        // Also verify UsageCount in the DB is still 0.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();
        var coupon = await db.Coupons.FirstAsync(c => c.Code == _couponCode);
        Assert.Equal(0, coupon.UsageCount);
    }

    [Then("the order should report the coupon as applied")]
    public void ThenOrderReportsCouponApplied()
    {
        Assert.NotNull(_orderResponse);
        Assert.True(
            _orderResponse!.CouponApplied,
            $"Expected the coupon to be applied; the response gave reason {_orderResponse.RejectionReason}.");
        Assert.Equal(_couponCode, _orderResponse.CouponCode);
        Assert.Null(_orderResponse.RejectionReason);
    }

    [Then(@"the order should report the coupon as not applied with reason ""([^""]*)""")]
    public void ThenOrderReportsCouponNotApplied(string reason)
    {
        Assert.NotNull(_orderResponse);
        Assert.False(_orderResponse!.CouponApplied);
        Assert.Equal(reason, _orderResponse.RejectionReason);

        // CouponCode is documented as present only when the coupon was actually
        // redeemed, so a rejected one must not come back looking applied.
        Assert.Null(_orderResponse.CouponCode);
    }

    [Then(@"the order total should be (\d+(?:\.\d+)?) with a discount of (\d+(?:\.\d+)?)")]
    public void ThenOrderTotals(decimal total, decimal discount)
    {
        Assert.NotNull(_orderResponse);
        Assert.Equal(total, _orderResponse!.Total);
        Assert.Equal(discount, _orderResponse.DiscountAmount);
    }

    [Then(@"the coupon usage count should be (\d+)")]
    public async Task ThenCouponUsageCount(int expected)
    {
        // Read back from the database rather than inferred from the response. The
        // response says whether the coupon was applied; only this says whether rule 4's
        // single atomic UPDATE actually ran.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();
        var coupon = await db.Coupons.AsNoTracking().FirstAsync(c => c.Code == _couponCode);

        Assert.Equal(expected, coupon.UsageCount);
    }

    [Then("a redemption should be recorded against that order")]
    public async Task ThenRedemptionRecorded()
    {
        Assert.NotNull(_orderResponse);

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();

        // SingleAsync, not FirstAsync: one order consumes exactly one redemption, and a
        // duplicate row would mean the transaction ran twice.
        var redemption = await db.CouponRedemptions
            .AsNoTracking()
            .SingleAsync(r => r.OrderId == _orderResponse!.OrderId);

        Assert.Equal(_couponCode, redemption.CouponCode);
    }

    [Then("no redemption should be recorded")]
    public async Task ThenNoRedemptionRecorded()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();

        Assert.Empty(await db.CouponRedemptions.AsNoTracking().ToListAsync());
    }

    [Then(@"the order total should reflect the server price of (\d+(?:\.\d+)?)")]
    public async Task ThenTotalReflectsServerPrice(decimal serverPrice)
    {
        Assert.NotNull(_orderResponse);
        Assert.Equal(serverPrice, _orderResponse!.Total);

        // The stored line must carry the server price too, not just the response total.
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PizzaShopDbContext>();
        var line = await db.OrderLines.FirstAsync(l => l.OrderId == _orderResponse.OrderId);
        Assert.Equal(serverPrice, line.UnitPrice);
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        _rawResponse?.Dispose();
        _client?.Dispose();
        _factory?.Dispose();
    }
}

