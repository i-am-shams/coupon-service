using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PizzaShop.Infrastructure.Entities;
using PizzaShop.Coupons;

namespace PizzaShop.Infrastructure.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<OrderEntity>
{
    public void Configure(EntityTypeBuilder<OrderEntity> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.CreatedAt).IsRequired();
        builder.Property(o => o.Subtotal).HasColumnType("decimal(18,2)");
        builder.Property(o => o.DiscountAmount).HasColumnType("decimal(18,2)");
        builder.Property(o => o.Total).HasColumnType("decimal(18,2)");
        builder.Property(o => o.CouponCode).HasMaxLength(CouponCodeRules.MaxLength);
        builder.HasMany(o => o.Lines)
               .WithOne(l => l.Order)
               .HasForeignKey(l => l.OrderId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
