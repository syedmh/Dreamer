namespace Husaynia.Application.Contracts;

public static class RoleNames
{
    public const string SiteAdministrator = nameof(SiteAdministrator);
    public const string ContentEditor = nameof(ContentEditor);
    public const string EventEditor = nameof(EventEditor);
    public const string MediaEditor = nameof(MediaEditor);
    public const string DonationOperator = nameof(DonationOperator);
    public const string ReadOnlyAuditor = nameof(ReadOnlyAuditor);

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SiteAdministrator,
        ContentEditor,
        EventEditor,
        MediaEditor,
        DonationOperator,
        ReadOnlyAuditor,
    };
}

public static class PolicyNames
{
    public const string SiteAdministration = nameof(SiteAdministration);
    public const string ContentManagement = nameof(ContentManagement);
    public const string EventManagement = nameof(EventManagement);
    public const string MediaManagement = nameof(MediaManagement);
    public const string DonationOperations = nameof(DonationOperations);
    public const string AuditRead = nameof(AuditRead);

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SiteAdministration,
        ContentManagement,
        EventManagement,
        MediaManagement,
        DonationOperations,
        AuditRead,
    };
}

[Flags]
public enum CapabilityAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Audit = 4,
    Reports = 8,
    Limited = 16,
}

public enum AdministrativeCapability
{
    UsersRolesIntegrationsSettings,
    PagesAnnouncementsReligiousContent,
    EventsAndIcal,
    MediaAndAliases,
    DonationStatusAndReconciliation,
    AuditAndOperationalReports,
}

public static class AuthorizationContract
{
    private static readonly IReadOnlyDictionary<AdministrativeCapability, IReadOnlyDictionary<string, CapabilityAccess>>
        capabilityMatrix = BuildCapabilityMatrix();

    public const bool PublicRegistrationEnabled = false;

    public const bool MfaRequiredForPrivilegedRoles = true;

    public static IReadOnlyDictionary<AdministrativeCapability, IReadOnlyDictionary<string, CapabilityAccess>>
        CapabilityMatrix => capabilityMatrix;

    public const bool AuditAllowedAndDeniedPrivilegedAttempts = true;

    private static Dictionary<AdministrativeCapability, IReadOnlyDictionary<string, CapabilityAccess>>
        BuildCapabilityMatrix() =>
        new()
        {
            [AdministrativeCapability.UsersRolesIntegrationsSettings] = Access(
                (RoleNames.SiteAdministrator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.ReadOnlyAuditor, CapabilityAccess.Reports | CapabilityAccess.Read)),
            [AdministrativeCapability.PagesAnnouncementsReligiousContent] = Access(
                (RoleNames.SiteAdministrator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.ContentEditor, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.ReadOnlyAuditor, CapabilityAccess.Audit | CapabilityAccess.Read)),
            [AdministrativeCapability.EventsAndIcal] = Access(
                (RoleNames.SiteAdministrator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.EventEditor, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.ReadOnlyAuditor, CapabilityAccess.Audit | CapabilityAccess.Read)),
            [AdministrativeCapability.MediaAndAliases] = Access(
                (RoleNames.SiteAdministrator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.MediaEditor, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.ReadOnlyAuditor, CapabilityAccess.Audit | CapabilityAccess.Read)),
            [AdministrativeCapability.DonationStatusAndReconciliation] = Access(
                (RoleNames.SiteAdministrator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.DonationOperator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.ReadOnlyAuditor, CapabilityAccess.Reports | CapabilityAccess.Read)),
            [AdministrativeCapability.AuditAndOperationalReports] = Access(
                (RoleNames.SiteAdministrator, CapabilityAccess.Read | CapabilityAccess.Write),
                (RoleNames.DonationOperator, CapabilityAccess.Limited | CapabilityAccess.Read),
                (RoleNames.ReadOnlyAuditor, CapabilityAccess.Read)),
        };

    private static Dictionary<string, CapabilityAccess> Access(
        params (string Role, CapabilityAccess Access)[] entries) =>
        entries.ToDictionary(entry => entry.Role, entry => entry.Access, StringComparer.Ordinal);
}
