using WillVault.Domain.Common;

namespace WillVault.Domain.Entities;

public class VaultItemRecipient : BaseEntity
{
    public Guid VaultItemId { get; set; }
    public Guid RecipientId { get; set; }

    /// <summary>
    /// Priority order for delivery (lower = higher priority).
    /// </summary>
    public int DeliveryPriority { get; set; }

    /// <summary>
    /// Optional delay after death verification before delivering this item.
    /// For example, "deliver 7 days after verification".
    /// </summary>
    public TimeSpan? ScheduledDeliveryDelay { get; set; }

    // Navigation properties
    public VaultItem VaultItem { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;
}
