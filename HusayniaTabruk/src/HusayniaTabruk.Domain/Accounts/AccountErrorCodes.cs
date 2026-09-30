using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Domain.Accounts;

public static class AccountErrorCodes
{
    public const string InvalidMembershipTransition = "membership_invalid_transition";
    public const string InvalidRoleTransition = "role_invalid_transition";
    public const string RoleParticipantsNotDistinct = "role_participants_not_distinct";
    public const string ActiveMembershipRequired = "active_membership_required";
    public const string AdministratorRoleRequired = "administrator_role_required";
    public const string OrganizationMismatch = "organization_mismatch";
    public const string RoleChangeRequestExpired = "role_change_request_expired";
    public const string MinimumAdministratorsRequired = "minimum_administrators_required";
    public const string BootstrapRequiresTwoAdministrators = "bootstrap_requires_two_administrators";
    public const string BootstrapSealed = "bootstrap_sealed";
    public const string InvalidAccountInput = "invalid_account_input";
    public const string InvalidAccountGovernanceState = "invalid_account_governance_state";
    public const string InvalidAdministratorRoleChangeAction = "invalid_administrator_role_change_action";
    public const string RoleChangeRequestChronologyInvalid = "role_change_request_chronology_invalid";

    internal static DomainError Conflict(string code, string message) => DomainError.Conflict(code, message);
    internal static DomainError Validation(string code, string message) => DomainError.Validation(code, message);
}
