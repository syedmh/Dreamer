namespace Khilat.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
