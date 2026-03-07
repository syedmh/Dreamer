using System.ComponentModel.DataAnnotations;

namespace ShareApp.Web.Entities;

public class Message
{
    public int Id { get; set; }

    [Required]
    public string SenderId { get; set; } = string.Empty;

    [Required]
    public string ReceiverId { get; set; } = string.Empty;

    [Required]
    [MaxLength(2000)]
    public string Content { get; set; } = string.Empty;

    public bool IsRead { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReadAt { get; set; }

    // Optional: Link to item or transaction
    public int? ItemId { get; set; }
    public int? TransactionId { get; set; }

    // Navigation properties
    public virtual ApplicationUser Sender { get; set; } = null!;
    public virtual ApplicationUser Receiver { get; set; } = null!;
    public virtual Item? Item { get; set; }
    public virtual Transaction? Transaction { get; set; }
}
