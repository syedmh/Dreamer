using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;

namespace Husaynia.Application.Tests.Identity;

public sealed class AdministrativeCapabilityAuthorizerTests
{
    private readonly AdministrativeCapabilityAuthorizer authorizer = new();

    [Fact]
    public void CanEnterCapabilityRequiresAuthentication()
    {
        var actor = new AdministrativeRequestActor(
            false,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            false,
            "corr-anon");

        var result = authorizer.CanEnterCapability(
            actor,
            AdministrativeCapability.PagesAnnouncementsReligiousContent);

        Assert.False(result.Allowed);
        Assert.Equal("unauthenticated", result.ErrorCode);
    }

    [Fact]
    public void CanEnterCapabilityRequiresMfaForPrivilegedRole()
    {
        var actor = new AdministrativeRequestActor(
            true,
            "user-1",
            new HashSet<string>(StringComparer.Ordinal) { RoleNames.ContentEditor },
            false,
            "corr-no-mfa");

        var result = authorizer.CanEnterCapability(
            actor,
            AdministrativeCapability.PagesAnnouncementsReligiousContent);

        Assert.False(result.Allowed);
        Assert.Equal("mfa_required", result.ErrorCode);
        Assert.Equal(CapabilityAccess.Read | CapabilityAccess.Write, result.GrantedAccess);
    }

    [Fact]
    public void AuthorizeRejectsLimitedDonationOperatorForFullAuditRead()
    {
        var actor = new AdministrativeRequestActor(
            true,
            "user-2",
            new HashSet<string>(StringComparer.Ordinal) { RoleNames.DonationOperator },
            true,
            "corr-limited");

        var result = authorizer.Authorize(
            actor,
            AdministrativeCapability.AuditAndOperationalReports,
            CapabilityAccess.Read,
            allowLimited: false);

        Assert.False(result.Allowed);
        Assert.Equal("limited_access", result.ErrorCode);
        Assert.Equal(CapabilityAccess.Read | CapabilityAccess.Limited, result.GrantedAccess);
    }

    [Theory]
    [InlineData(RoleNames.SiteAdministrator, AdministrativeCapability.UsersRolesIntegrationsSettings, CapabilityAccess.Read | CapabilityAccess.Write)]
    [InlineData(RoleNames.ContentEditor, AdministrativeCapability.PagesAnnouncementsReligiousContent, CapabilityAccess.Read | CapabilityAccess.Write)]
    [InlineData(RoleNames.EventEditor, AdministrativeCapability.EventsAndIcal, CapabilityAccess.Read | CapabilityAccess.Write)]
    [InlineData(RoleNames.MediaEditor, AdministrativeCapability.MediaAndAliases, CapabilityAccess.Read | CapabilityAccess.Write)]
    [InlineData(RoleNames.DonationOperator, AdministrativeCapability.DonationStatusAndReconciliation, CapabilityAccess.Read | CapabilityAccess.Write)]
    [InlineData(RoleNames.ReadOnlyAuditor, AdministrativeCapability.AuditAndOperationalReports, CapabilityAccess.Read)]
    public void AuthorizeAllowsExpectedRoleCapabilityPairs(
        string role,
        AdministrativeCapability capability,
        CapabilityAccess expectedAccess)
    {
        var actor = new AdministrativeRequestActor(
            true,
            "user-3",
            new HashSet<string>(StringComparer.Ordinal) { role },
            true,
            "corr-allow");

        var result = authorizer.Authorize(actor, capability, CapabilityAccess.Read, allowLimited: true);

        Assert.True(result.Allowed);
        Assert.Equal(expectedAccess, result.GrantedAccess & expectedAccess);
    }
}
