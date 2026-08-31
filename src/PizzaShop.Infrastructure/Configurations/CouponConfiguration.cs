using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PizzaShop.Infrastructure.Entities;
using PizzaShop.Coupons;

namespace PizzaShop.Infrastructure.Configurations;

public sealed class CouponConfiguration : IEntityTypeConfiguration<CouponEntity>
{
    public void Configure(EntityTypeBuilder<CouponEntity> builder)
    {
        builder.ToTable("Coupons");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).IsRequired().HasMaxLength(CouponCodeRules.MaxLength);
        builder.HasIndex(c => c.Code).IsUnique();
        builder.Property(c => c.Type).IsRequired();
        builder.Property(c => c.Value).HasColumnType("decimal(18,2)");
        builder.Property(c => c.Description).IsRequired().HasMaxLength(200);
        builder.Property(c => c.ExpiresAt).IsRequired();
        builder.Property(c => c.MinimumOrderValue).HasColumnType("decimal(18,2)");
        builder.Property(c => c.RedemptionLimit).IsRequired();
        builder.Property(c => c.UsageCount).IsRequired().HasDefaultValue(0);
    }
}
