using Husaynia.Application.Contracts;

namespace Husaynia.Application.Tests;

public sealed class FrozenContractTests
{
    private static readonly string[] ExpectedRoles =
    [
        "SiteAdministrator",
        "ContentEditor",
        "EventEditor",
        "MediaEditor",
        "DonationOperator",
        "ReadOnlyAuditor",
    ];

    private static readonly string[] ExpectedPolicies =
    [
        "SiteAdministration",
        "ContentManagement",
        "EventManagement",
        "MediaManagement",
        "DonationOperations",
        "AuditRead",
    ];

    [Fact]
    public void AuthorizationContractContainsExactFrozenRolesAndPolicies()
    {
        Assert.Equal(ExpectedRoles.Order(StringComparer.Ordinal), RoleNames.All.Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedPolicies.Order(StringComparer.Ordinal), PolicyNames.All.Order(StringComparer.Ordinal));
        Assert.False(AuthorizationContract.PublicRegistrationEnabled);
        Assert.True(AuthorizationContract.MfaRequiredForPrivilegedRoles);
        Assert.True(AuthorizationContract.AuditAllowedAndDeniedPrivilegedAttempts);
        Assert.Equal(6, AuthorizationContract.CapabilityMatrix.Count);
    }

    [Fact]
    public void AuthorizationMatrixMatchesEveryFrozenCellAndDeniesOmittedRoles()
    {
        var expected = new Dictionary<AdministrativeCapability, IReadOnlyDictionary<string, CapabilityAccess>>
        {
            [AdministrativeCapability.UsersRolesIntegrationsSettings] = Access(
                ("SiteAdministrator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("ReadOnlyAuditor", CapabilityAccess.Reports | CapabilityAccess.Read)),
            [AdministrativeCapability.PagesAnnouncementsReligiousContent] = Access(
                ("SiteAdministrator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("ContentEditor", CapabilityAccess.Read | CapabilityAccess.Write),
                ("ReadOnlyAuditor", CapabilityAccess.Audit | CapabilityAccess.Read)),
            [AdministrativeCapability.EventsAndIcal] = Access(
                ("SiteAdministrator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("EventEditor", CapabilityAccess.Read | CapabilityAccess.Write),
                ("ReadOnlyAuditor", CapabilityAccess.Audit | CapabilityAccess.Read)),
            [AdministrativeCapability.MediaAndAliases] = Access(
                ("SiteAdministrator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("MediaEditor", CapabilityAccess.Read | CapabilityAccess.Write),
                ("ReadOnlyAuditor", CapabilityAccess.Audit | CapabilityAccess.Read)),
            [AdministrativeCapability.DonationStatusAndReconciliation] = Access(
                ("SiteAdministrator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("DonationOperator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("ReadOnlyAuditor", CapabilityAccess.Reports | CapabilityAccess.Read)),
            [AdministrativeCapability.AuditAndOperationalReports] = Access(
                ("SiteAdministrator", CapabilityAccess.Read | CapabilityAccess.Write),
                ("DonationOperator", CapabilityAccess.Limited | CapabilityAccess.Read),
                ("ReadOnlyAuditor", CapabilityAccess.Read)),
        };

        foreach (var capability in Enum.GetValues<AdministrativeCapability>())
        {
            var actual = AuthorizationContract.CapabilityMatrix[capability];
            Assert.All(actual.Keys, role => Assert.Contains(role, ExpectedRoles));

            foreach (var role in ExpectedRoles)
            {
                var expectedAccess = expected[capability].GetValueOrDefault(role, CapabilityAccess.None);
                var actualAccess = actual.GetValueOrDefault(role, CapabilityAccess.None);
                Assert.Equal(expectedAccess, actualAccess);
            }

            Assert.Equal(CapabilityAccess.None, actual.GetValueOrDefault("OrdinaryAuthenticatedUser"));
            Assert.Equal(CapabilityAccess.None, actual.GetValueOrDefault("UnknownRole"));
        }
    }

    [Fact]
    public void ResultExposesOnlyTheActiveBranch()
    {
        var success = Result.Succeed<string, ContractError>("ok");
        var failure = Result.Fail<string, ContractError>(new ContractError("invalid", "Invalid."));

        Assert.Equal("ok", success.Success);
        Assert.Throws<InvalidOperationException>(() => success.Error);
        Assert.Equal("invalid", failure.Error.Code);
        Assert.Throws<InvalidOperationException>(() => failure.Success);
    }

    private static Dictionary<string, CapabilityAccess> Access(
        params (string Role, CapabilityAccess Access)[] entries) =>
        entries.ToDictionary(entry => entry.Role, entry => entry.Access, StringComparer.Ordinal);
}
