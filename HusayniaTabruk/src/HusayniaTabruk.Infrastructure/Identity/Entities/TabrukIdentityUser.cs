using Microsoft.AspNetCore.Identity;

namespace HusayniaTabruk.Infrastructure.Identity.Entities;

/// <summary>
/// Identity storage is deliberately limited to authentication data. Membership profile and
/// organization authorization state are stored in the operational persistence model.
/// </summary>
public sealed class TabrukIdentityUser : IdentityUser<Guid>
{
}
