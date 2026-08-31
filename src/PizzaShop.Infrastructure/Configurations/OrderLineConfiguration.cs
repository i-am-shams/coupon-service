using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Infrastructure.Configurations;

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLineEntity>
{
    public void Configure(EntityTypeBuilder<OrderLineEntity> builder)
    {
        builder.ToTable("OrderLines");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Quantity).IsRequired();
        builder.Property(l => l.UnitPrice).HasColumnType("decimal(18,2)");
    }
}
