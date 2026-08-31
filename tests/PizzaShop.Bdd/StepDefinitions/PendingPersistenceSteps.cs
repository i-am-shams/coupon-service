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

    // ── Then ──────────────────────────────────────────────────────────────

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
        _client?.Dispose();
        _factory?.Dispose();
    }
}

