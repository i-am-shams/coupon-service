using PizzaShop.Coupons;
using PizzaShop.Infrastructure.Repositories;

namespace PizzaShop.Infrastructure.Adapters;

/// <summary>
/// Implements ICouponEvaluator by loading live coupon data from the repository on
/// each call and delegating to the stateless CouponEvaluator for the rules logic.
/// The rules are unchanged — only the source of their catalogue changes.
/// </summary>
public sealed class DatabaseCouponEvaluator : ICouponEvaluator
{
    private readonly ICouponRepository _repository;

    public DatabaseCouponEvaluator(ICouponRepository repository) => _repository = repository;

    public async Task<CouponEvaluation> EvaluateAsync(
        string code,
        CouponBasket basket,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        var catalogue = await _repository.GetAllAsync(cancellationToken);
        return new CouponEvaluator(catalogue).Evaluate(code, basket, asOf);
    }
}
