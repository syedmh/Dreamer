using Husaynia.Application.Contracts;
using System.Diagnostics.CodeAnalysis;

namespace Husaynia.Application.Identity;

public sealed class AdministrativeCapabilityAuthorizer
{
    private static readonly Dictionary<AdministrativeCapability, string> PolicyNamesByCapability =
        new()
        {
            [AdministrativeCapability.UsersRolesIntegrationsSettings] = PolicyNames.SiteAdministration,
            [AdministrativeCapability.PagesAnnouncementsReligiousContent] = PolicyNames.ContentManagement,
            [AdministrativeCapability.EventsAndIcal] = PolicyNames.EventManagement,
            [AdministrativeCapability.MediaAndAliases] = PolicyNames.MediaManagement,
            [AdministrativeCapability.DonationStatusAndReconciliation] = PolicyNames.DonationOperations,
            [AdministrativeCapability.AuditAndOperationalReports] = PolicyNames.AuditRead,
        };

    public static string GetPolicyName(AdministrativeCapability capability) =>
        PolicyNamesByCapability[capability];

    public static IReadOnlySet<string> GetCapabilityRoles(AdministrativeCapability capability) =>
        AuthorizationContract.CapabilityMatrix[capability].Keys.ToHashSet(StringComparer.Ordinal);

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The authorizer is an injected policy service and intentionally exposes instance methods.")]
    public CapabilityEvaluation CanEnterCapability(
        AdministrativeRequestActor actor,
        AdministrativeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!actor.IsAuthenticated)
        {
            return Denied(
                "unauthenticated",
                "Authentication is required to access administration.",
                CapabilityAccess.None);
        }

        var granted = GetGrantedAccess(actor.Roles, capability);
        if (granted == CapabilityAccess.None)
        {
            return Denied(
                "forbidden",
                "The signed-in user does not have access to this administrative capability.",
                CapabilityAccess.None);
        }

        if (!actor.HasSatisfiedMfa)
        {
            return Denied(
                "mfa_required",
                "Multi-factor authentication is required for privileged administration.",
                granted);
        }

        return Allowed(granted);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The authorizer is an injected policy service and intentionally exposes instance methods.")]
    public CapabilityEvaluation Authorize(
        AdministrativeRequestActor actor,
        AdministrativeCapability capability,
        CapabilityAccess requiredAccess,
        bool allowLimited)
    {
        var capabilityEvaluation = CanEnterCapability(actor, capability);
        if (!capabilityEvaluation.Allowed)
        {
            return capabilityEvaluation;
        }

        if ((capabilityEvaluation.GrantedAccess & requiredAccess) != requiredAccess)
        {
            return Denied(
                "forbidden",
                "The signed-in user does not have the required access for this administrative operation.",
                capabilityEvaluation.GrantedAccess);
        }

        if (!allowLimited && capabilityEvaluation.GrantedAccess.HasFlag(CapabilityAccess.Limited))
        {
            return Denied(
                "limited_access",
                "This administrative operation requires full access.",
                capabilityEvaluation.GrantedAccess);
        }

        return capabilityEvaluation;
    }

    public static CapabilityAccess GetGrantedAccess(
        IReadOnlySet<string> roles,
        AdministrativeCapability capability)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var matrix = AuthorizationContract.CapabilityMatrix[capability];
        var granted = CapabilityAccess.None;
        foreach (var role in roles)
        {
            granted |= matrix.GetValueOrDefault(role, CapabilityAccess.None);
        }

        return granted;
    }

    private static CapabilityEvaluation Allowed(CapabilityAccess grantedAccess) =>
        new(true, string.Empty, string.Empty, grantedAccess);

    private static CapabilityEvaluation Denied(
        string errorCode,
        string message,
        CapabilityAccess grantedAccess) =>
        new(false, errorCode, message, grantedAccess);
}

public sealed record CapabilityEvaluation(
    bool Allowed,
    string ErrorCode,
    string Message,
    CapabilityAccess GrantedAccess);
