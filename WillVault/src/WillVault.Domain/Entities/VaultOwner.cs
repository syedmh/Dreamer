using WillVault.Domain.Common;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Entities;

public class VaultOwner : BaseEntity
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public AccountStatus AccountStatus { get; set; } = AccountStatus.Active;

    /// <summary>
    /// Maps to the ASP.NET Identity user ID in Infrastructure layer.
    /// </summary>
    public string IdentityUserId { get; set; } = string.Empty;

    // Navigation properties
    public ICollection<VaultItem> VaultItems { get; set; } = new List<VaultItem>();
    public ICollection<Recipient> Recipients { get; set; } = new List<Recipient>();
    public ICollection<TrustedContact> TrustedContacts { get; set; } = new List<TrustedContact>();
    public ICollection<DeathVerificationRequest> DeathVerificationRequests { get; set; } = new List<DeathVerificationRequest>();

    public bool IsActive => AccountStatus == AccountStatus.Active;

    public bool CanTransitionTo(AccountStatus newStatus)
    {
        return (AccountStatus, newStatus) switch
        {
            (AccountStatus.Active, AccountStatus.VerificationPending) => true,
            (AccountStatus.VerificationPending, AccountStatus.Verified) => true,
            (AccountStatus.VerificationPending, AccountStatus.Active) => true, // Rejected → back to active
            (AccountStatus.Verified, AccountStatus.ReleaseScheduled) => true,
            (AccountStatus.Verified, AccountStatus.VerificationPending) => true, // Disputed → re-review
            (AccountStatus.ReleaseScheduled, AccountStatus.Releasing) => true,
            (AccountStatus.ReleaseScheduled, AccountStatus.VerificationPending) => true, // Disputed before release
            (AccountStatus.Releasing, AccountStatus.Closed) => true,
            _ => false
        };
    }

    public void TransitionTo(AccountStatus newStatus)
    {
        if (!CanTransitionTo(newStatus))
            throw new InvalidOperationException(
                $"Cannot transition from {AccountStatus} to {newStatus}.");

        AccountStatus = newStatus;
        UpdatedAt = DateTime.UtcNow;
    }
}
