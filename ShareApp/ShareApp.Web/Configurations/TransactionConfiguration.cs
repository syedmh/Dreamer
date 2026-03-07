using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Amount)
            .HasPrecision(18, 2);

        builder.Property(t => t.StripePaymentIntentId)
            .HasMaxLength(500);

        builder.Property(t => t.StripeChargeId)
            .HasMaxLength(500);

        builder.Property(t => t.Status)
            .HasDefaultValue(TransactionStatus.Pending);

        builder.Property(t => t.Notes)
            .HasMaxLength(1000);

        builder.Property(t => t.CreatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        // Relationships
        builder.HasOne(t => t.Item)
            .WithMany(i => i.Transactions)
            .HasForeignKey(t => t.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Buyer)
            .WithMany(u => u.PurchasedTransactions)
            .HasForeignKey(t => t.BuyerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.Seller)
            .WithMany(u => u.SoldTransactions)
            .HasForeignKey(t => t.SellerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(t => t.BuyerId);
        builder.HasIndex(t => t.SellerId);
        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => t.CreatedAt);
    }
}
