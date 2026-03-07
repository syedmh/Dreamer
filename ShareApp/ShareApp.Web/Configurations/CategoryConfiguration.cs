using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(c => c.Description)
            .HasMaxLength(200);

        builder.Property(c => c.IconClass)
            .HasMaxLength(50);

        builder.Property(c => c.IsActive)
            .HasDefaultValue(true);

        // Seed data
        builder.HasData(
            new Category { Id = 1, Name = "Produce", Description = "Fresh fruits and vegetables", IconClass = "fa-carrot", IsActive = true },
            new Category { Id = 2, Name = "Baked Goods", Description = "Bread, cakes, and pastries", IconClass = "fa-bread-slice", IsActive = true },
            new Category { Id = 3, Name = "Prepared Food", Description = "Ready-to-eat meals", IconClass = "fa-utensils", IsActive = true },
            new Category { Id = 4, Name = "Pantry Items", Description = "Canned goods, dry goods, spices", IconClass = "fa-box", IsActive = true },
            new Category { Id = 5, Name = "Beverages", Description = "Drinks and beverage mixes", IconClass = "fa-glass-water", IsActive = true },
            new Category { Id = 6, Name = "Dairy & Eggs", Description = "Milk, cheese, yogurt, eggs", IconClass = "fa-cheese", IsActive = true },
            new Category { Id = 7, Name = "Other", Description = "Other food items", IconClass = "fa-ellipsis", IsActive = true }
        );

        // Indexes
        builder.HasIndex(c => c.Name).IsUnique();
    }
}
