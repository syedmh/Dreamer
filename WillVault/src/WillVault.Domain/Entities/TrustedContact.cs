using WillVault.Domain.Common;

namespace WillVault.Domain.Entities;

public class TrustedContact : BaseEntity
{
    public Guid OwnerId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Relationship { get; set; }

    public bool IsVerified { get; set; }
    public string? VerificationToken { get; set; }
    public DateTime? VerifiedAt { get; set; }

    // Navigation properties
    public VaultOwner Owner { get; set; } = null!;
}
