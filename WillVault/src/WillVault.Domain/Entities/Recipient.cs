using WillVault.Domain.Common;

namespace WillVault.Domain.Entities;

public class Recipient : BaseEntity
{
    public Guid OwnerId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Relationship { get; set; }

    public bool IsEmailVerified { get; set; }
    public bool IsPhoneVerified { get; set; }
    public string? EmailVerificationToken { get; set; }
    public string? PhoneVerificationToken { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime? PhoneVerifiedAt { get; set; }

    // Navigation properties
    public VaultOwner Owner { get; set; } = null!;
    public ICollection<VaultItemRecipient> VaultItemRecipients { get; set; } = new List<VaultItemRecipient>();

    public bool HasVerifiedContactMethod => IsEmailVerified || IsPhoneVerified;
}
