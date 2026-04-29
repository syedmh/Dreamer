using Microsoft.AspNetCore.Identity;

namespace WillVault.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    /// <summary>
    /// Links this identity user to a domain VaultOwner entity.
    /// Null until the owner profile is created after registration.
    /// </summary>
    public Guid? VaultOwnerId { get; set; }

    public string? RefreshToken { get; set; }
    public DateTime? RefreshTokenExpiryTime { get; set; }
}
