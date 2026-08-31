using Reqnroll;

namespace PizzaShop.Bdd.StepDefinitions;

/// <summary>
/// Placeholder step definitions for scenarios that require persistence (Phase B).
/// These are intentionally pending — they will be implemented when EF Core and
/// WebApplicationFactory are introduced.
/// </summary>
[Binding]
public sealed class PendingPersistenceSteps
{
    private readonly ScenarioContext _scenario;

    public PendingPersistenceSteps(ScenarioContext scenario)
    {
        _scenario = scenario;
    }

    [Given(@"a coupon ""([^""]*)"" with a redemption limit of (\d+)")]
    public void GivenCouponWithRedemptionLimit(string code, int limit) =>
        _scenario.Pending();

    [When(@"I preview the coupon ""([^""]*)"" three times")]
    public void WhenPreviewThreeTimes(string code) =>
        _scenario.Pending();

    [Then("the coupon should still be valid on the fourth preview")]
    public void ThenStillValidOnFourthPreview() =>
        _scenario.Pending();

    [Given(@"I submit an order claiming a pizza costs (\d+(?:\.\d+)?) but the server has it at (\d+(?:\.\d+)?)")]
    public void GivenOrderWithFakePrice(decimal clientPrice, decimal serverPrice) =>
        _scenario.Pending();

    [Then(@"the order total should reflect the server price of (\d+(?:\.\d+)?)")]
    public void ThenTotalReflectsServerPrice(decimal serverPrice) =>
        _scenario.Pending();
}
