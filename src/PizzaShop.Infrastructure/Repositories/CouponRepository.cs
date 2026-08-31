using Microsoft.EntityFrameworkCore;
using PizzaShop.Coupons;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Infrastructure.Repositories;

public sealed class CouponRepository : ICouponRepository
{
    private readonly PizzaShopDbContext _db;

    public CouponRepository(PizzaShopDbContext db) => _db = db;

    public async Task<IReadOnlyList<CouponRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Coupons
            .AsNoTracking()
            .Select(c => new CouponRecord(
                c.Code,
                c.Type == CouponTypeEntity.Percentage ? CouponType.Percentage : CouponType.FixedAmount,
                c.Value,
                c.Description,
                c.ExpiresAt,
                c.MinimumOrderValue,
                c.RedemptionLimit,
                c.UsageCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryRedeemAsync(string code, CancellationToken cancellationToken = default)
    {
        // Rule 4: single atomic UPDATE — no read before the write.
        var rows = await _db.Coupons
            .Where(c => c.Code == code && c.UsageCount < c.RedemptionLimit)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.UsageCount, c => c.UsageCount + 1),
                cancellationToken);

        return rows > 0;
    }
}
