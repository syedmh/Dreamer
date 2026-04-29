using WillVault.Domain.Common;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Entities;

public class DeathVerificationRequest : BaseEntity
{
    public Guid OwnerId { get; set; }
    public Guid SubmittedById { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Comma-separated paths to uploaded proof documents (death certificates, etc.)
    /// </summary>
    public string DocumentPaths { get; set; } = string.Empty;
    public string? SubmitterNotes { get; set; }

    public VerificationStatus Status { get; set; } = VerificationStatus.Pending;

    // First reviewer
    public Guid? ReviewedById { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewerNotes { get; set; }

    // Dual approval: second reviewer
    public bool RequiresSecondApproval { get; set; } = true;
    public Guid? SecondReviewerId { get; set; }
    public DateTime? SecondReviewedAt { get; set; }
    public string? SecondReviewerNotes { get; set; }

    /// <summary>
    /// 48-hour cool-down window after approval before delivery triggers.
    /// </summary>
    public DateTime? CooldownExpiresAt { get; set; }

    public string? RejectionReason { get; set; }
    public string? DisputeReason { get; set; }
    public Guid? DisputedById { get; set; }

    // Navigation properties
    public VaultOwner Owner { get; set; } = null!;
    public TrustedContact SubmittedBy { get; set; } = null!;

    public bool IsFullyApproved =>
        Status == VerificationStatus.Approved &&
        ReviewedById.HasValue &&
        (!RequiresSecondApproval || SecondReviewerId.HasValue);

    public bool IsCooldownExpired =>
        CooldownExpiresAt.HasValue && DateTime.UtcNow >= CooldownExpiresAt.Value;
}
