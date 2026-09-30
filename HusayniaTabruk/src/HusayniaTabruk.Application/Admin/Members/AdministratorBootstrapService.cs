using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Admin.Members;

public sealed class AdministratorBootstrapService(
    IUnitOfWork unitOfWork,
    IMembershipRepository membershipRepository,
    IClock clock)
{
    private const string BootstrapPurpose = "administrator_bootstrap";

    public ValueTask<Result<BootstrapAdministratorsResult>> ExecuteAsync(
        BootstrapAdministratorsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<string> reason = MemberAdministrationSupport.ValidateReason(command.Reason);
        if (reason.IsFailure)
        {
            return ValueTask.FromResult(
                Result.Failure<BootstrapAdministratorsResult>(reason.Error));
        }

        if (command.AdministratorMembershipIds is null || command.AdministratorMembershipIds.Count == 0)
        {
            return ValueTask.FromResult(
                Result.Failure<BootstrapAdministratorsResult>(
                    MemberAdministrationErrorCodes.Validation(
                        "At least two administrator memberships are required.")));
        }

        return unitOfWork.ExecuteAsync(
            token => ExecuteCoreAsync(command, reason.Value, token),
            cancellationToken);
    }

    private async ValueTask<Result<BootstrapAdministratorsResult>> ExecuteCoreAsync(
        BootstrapAdministratorsCommand command,
        string reason,
        CancellationToken cancellationToken)
    {
        Result<OrganizationAccountGovernance> governance = await membershipRepository.GetGovernanceAsync(
            command.OrganizationId,
            cancellationToken);
        if (governance.IsFailure)
        {
            return Result.Failure<BootstrapAdministratorsResult>(governance.Error);
        }

        List<Membership> administrators = [];
        foreach (MembershipId membershipId in command.AdministratorMembershipIds)
        {
            Result<Membership> membership = MemberAdministrationSupport.FindMembership(
                governance.Value,
                membershipId);
            if (membership.IsFailure)
            {
                return Result.Failure<BootstrapAdministratorsResult>(membership.Error);
            }

            administrators.Add(membership.Value);
        }

        string beforeState = MemberAdministrationSupport.Serialize(
            MemberAdministrationSupport.ToAuditState(governance.Value));
        Result<AdministratorBootstrapCompleted> bootstrap = governance.Value.BootstrapAdministrators(
            administrators,
            clock.UtcNow);
        if (bootstrap.IsFailure)
        {
            return Result.Failure<BootstrapAdministratorsResult>(bootstrap.Error);
        }

        MembershipId persistenceActorMembershipId = bootstrap.Value.AdministratorMembershipIds
            .Distinct()
            .First();
        Result saved = await membershipRepository.SaveGovernanceAsync(
            governance.Value,
            persistenceActorMembershipId,
            bootstrap.Value.OccurredAt,
            MemberAdministrationSupport.CreateEffects(
                [],
                [
                    MemberAdministrationSupport.CreateAuditEntry(
                        governance.Value.OrganizationId,
                        persistenceActorMembershipId,
                        "administrator_bootstrap_completed",
                        "organization",
                        governance.Value.OrganizationId.ToString(),
                        reason,
                        BootstrapPurpose,
                        beforeState,
                        MemberAdministrationSupport.Serialize(
                            MemberAdministrationSupport.ToAuditState(governance.Value)),
                        bootstrap.Value.OccurredAt),
                ]),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<BootstrapAdministratorsResult>(saved.Error);
        }

        return Result.Success(
            new BootstrapAdministratorsResult(
                bootstrap.Value.OrganizationId,
                bootstrap.Value.AdministratorMembershipIds,
                bootstrap.Value.OccurredAt,
                governance.Value.Version));
    }
}
