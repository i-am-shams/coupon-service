using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Infrastructure.Configurations;

public sealed class CouponRedemptionConfiguration : IEntityTypeConfiguration<CouponRedemptionEntity>
{
    public void Configure(EntityTypeBuilder<CouponRedemptionEntity> builder)
    {
        builder.ToTable("CouponRedemptions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.CouponCode).IsRequired().HasMaxLength(50);
        builder.Property(r => r.RedeemedAt).IsRequired();
        builder.HasOne(r => r.Order)
               .WithMany()
               .HasForeignKey(r => r.OrderId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
