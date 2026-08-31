using PizzaShop.Coupons;
using PizzaShop.Infrastructure.Repositories;
using PizzaShop.Ordering;

namespace PizzaShop.Infrastructure.Adapters;

/// <summary>
/// Implements ICouponEvaluator by loading live coupon data from the repository on
/// each call and delegating to the stateless CouponEvaluator for the rules logic.
/// The evaluator itself is unchanged — only the source of its catalogue changes.
/// </summary>
public sealed class DatabaseCouponEvaluator : ICouponEvaluator
{
    private readonly ICouponRepository _repository;

    public DatabaseCouponEvaluator(ICouponRepository repository) => _repository = repository;

    public CouponEvaluation Evaluate(string code, CouponBasket basket, DateTimeOffset asOf)
    {
        var catalogue = _repository.GetAll();
        var evaluator = new CouponEvaluator(catalogue);
        return evaluator.Evaluate(code, basket, asOf);
    }
}
