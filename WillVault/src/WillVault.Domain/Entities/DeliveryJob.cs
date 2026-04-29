using WillVault.Domain.Common;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Entities;

public class DeliveryJob : BaseEntity
{
    public Guid VaultItemId { get; set; }
    public Guid RecipientId { get; set; }
    public DeliveryChannel Channel { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    /// <summary>
    /// Prevents duplicate deliveries across retries.
    /// </summary>
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString();

    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime? DeliveredAt { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Scheduled delivery time (respects VaultItemRecipient.ScheduledDeliveryDelay).
    /// </summary>
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public VaultItem VaultItem { get; set; } = null!;
    public Recipient Recipient { get; set; } = null!;

    public bool CanRetry => Attempts < MaxAttempts && Status == DeliveryStatus.Failed;

    public void RecordAttempt(bool success, string? error = null)
    {
        Attempts++;
        LastAttemptAt = DateTime.UtcNow;

        if (success)
        {
            Status = DeliveryStatus.Delivered;
            DeliveredAt = DateTime.UtcNow;
        }
        else
        {
            ErrorMessage = error;
            if (CanRetry)
            {
                Status = DeliveryStatus.Failed;
                // Exponential backoff: 1min, 5min, 25min, 2h, 10h
                var delayMinutes = Math.Pow(5, Attempts - 1);
                NextRetryAt = DateTime.UtcNow.AddMinutes(delayMinutes);
            }
            else
            {
                Status = DeliveryStatus.Bounced;
            }
        }

        UpdatedAt = DateTime.UtcNow;
    }
}
