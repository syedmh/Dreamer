namespace HusayniaTabruk.Domain.Common.Enums;

public enum MembershipStatus
{
    Invited,
    Active,
    Disabled,
}

public enum OrganizationRole
{
    Admin,
    FoodIncharge,
}

#pragma warning disable CA1711 // The frozen architecture contract names this enum PrivilegedPermission.
public enum PrivilegedPermission
{
    PrivilegedThreadRead,
}
#pragma warning restore CA1711

public enum ServiceDateStatus
{
    Draft,
    Open,
    Closed,
    Cancelled,
    Completed,
}

public enum HelpCategory
{
    FoodPreparation,
    Serving,
    Cleanup,
}

public enum HelpNeedStatus
{
    Open,
    Closed,
}

public enum SignupKind
{
    Individual,
    Household,
    Team,
}

public enum SignupStatus
{
    Pending,
    Approved,
    Waitlisted,
    Declined,
    Withdrawn,
    Cancelled,
}

public enum ThreadStatus
{
    Open,
    Locked,
}

public enum MessageVisibility
{
    Visible,
    Hidden,
}
