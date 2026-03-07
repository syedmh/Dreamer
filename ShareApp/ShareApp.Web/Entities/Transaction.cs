using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Entities;

public class Transaction
{
    public int Id { get; set; }

    [Required]
    public int ItemId { get; set; }

    [Required]
    public string BuyerId { get; set; } = string.Empty;

    [Required]
    public string SellerId { get; set; } = string.Empty;

    public TransactionType TransactionType { get; set; } = TransactionType.Purchase;

    public decimal? Amount { get; set; }

    [MaxLength(500)]
    public string? StripePaymentIntentId { get; set; }

    [MaxLength(500)]
    public string? StripeChargeId { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Pending;

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    // Navigation properties
    public virtual Item Item { get; set; } = null!;
    public virtual ApplicationUser Buyer { get; set; } = null!;
    public virtual ApplicationUser Seller { get; set; } = null!;
}
