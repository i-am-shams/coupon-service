using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PizzaShop.Infrastructure.Entities;

namespace PizzaShop.Infrastructure.Configurations;

public sealed class PizzaConfiguration : IEntityTypeConfiguration<PizzaEntity>
{
    public void Configure(EntityTypeBuilder<PizzaEntity> builder)
    {
        builder.ToTable("Pizzas");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(100);
        builder.Property(p => p.Description).IsRequired().HasMaxLength(500);
        builder.Property(p => p.Price).HasColumnType("decimal(18,2)");
    }
}
