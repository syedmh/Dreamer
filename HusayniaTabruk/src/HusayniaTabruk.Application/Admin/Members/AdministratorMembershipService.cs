using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Application.Admin.Members;

public sealed class AdministratorMembershipService(
    IUnitOfWork unitOfWork,
    IMembershipRepository membershipRepository,
    ICurrentActor currentActor,
    IStepUpVerifier stepUpVerifier,
    IClock clock)
{
    private const string GovernancePurpose = "governance";
    private const string MembershipAdministrationPurpose = "membership_administration";
    private const string PendingInviteDisplayName = "Pending invite";

    public async ValueTask<Result<AdminMembersPage>> ListAsync(
        ListAdminMembersCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<(int StartIndex, int PageSize)> page = MemberAdministrationSupport.ValidatePage(
            command.Cursor,
            command.PageSize);
        if (page.IsFailure)
        {
            return Result.Failure<AdminMembersPage>(page.Error);
        }

        Result<OrganizationAccountGovernance> governance = await membershipRepository.GetGovernanceAsync(
            currentActor.OrganizationId,
            cancellationToken);
        if (governance.IsFailure)
        {
            return Result.Failure<AdminMembersPage>(governance.Error);
        }

        Result<Membership> actor = MemberAdministrationSupport.ResolveAdministrator(
            governance.Value,
            currentActor);
        if (actor.IsFailure)
        {
            return Result.Failure<AdminMembersPage>(actor.Error);
        }

        Dictionary<MembershipId, IReadOnlyCollection<AdminRoleChangeRequestSummary>> requestsByTarget =
            governance.Value.RoleChangeRequests
                .Where(request => request.Status == RoleChangeRequestStatus.Pending)
                .GroupBy(request => request.TargetMembershipId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyCollection<AdminRoleChangeRequestSummary>)group
                        .OrderBy(request => request.ProposedAt)
                        .ThenBy(request => request.Id.Value)
                        .Select(MemberAdministrationSupport.ToSummary)
                        .ToArray());

        AdminMemberSummary[] orderedMembers = governance.Value.Memberships
            .OrderBy(membership => membership.DisplayName, StringComparer.Ordinal)
            .ThenBy(membership => membership.Id.Value)
            .Select(
                membership => MemberAdministrationSupport.ToSummary(
                    membership,
                    requestsByTarget.GetValueOrDefault(membership.Id, Array.Empty<AdminRoleChangeRequestSummary>())))
            .ToArray();

        int startIndex = Math.Min(page.Value.StartIndex, orderedMembers.Length);
        int pageSize = page.Value.PageSize;
        AdminMemberSummary[] items = orderedMembers
            .Skip(startIndex)
            .Take(pageSize)
            .ToArray();
        string? nextCursor = MemberAdministrationSupport.EncodeCursor(
            startIndex + items.Length,
            orderedMembers.Length);

        return Result.Success(
            new AdminMembersPage(
                governance.Value.Version,
                items,
                nextCursor));
    }

    public ValueTask<Result<IssuedMembershipInvitation>> IssueInvitationAsync(
        IssueMembershipInvitationCommand command,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<(string Email, int ExpiresInHours)> validation =
            MemberAdministrationSupport.ValidateInvitation(command);
        if (validation.IsFailure)
        {
            return ValueTask.FromResult(
                Result.Failure<IssuedMembershipInvitation>(validation.Error));
        }

        return unitOfWork.ExecuteAsync(
            token => IssueInvitationCoreAsync(
                validation.Value.Email,
                validation.Value.ExpiresInHours,
                stepUpToken,
                token),
            cancellationToken);
    }

    public ValueTask<Result<long>> AssignFoodInchargeAsync(
        AssignFoodInchargeCommand command,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteFoodInchargeChangeAsync(
            command.TargetMembershipId,
            command.Reason,
            expectedVersion,
            stepUpToken,
            grant: true,
            cancellationToken);
    }

    public ValueTask<Result<long>> RevokeFoodInchargeAsync(
        RevokeFoodInchargeCommand command,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteFoodInchargeChangeAsync(
            command.TargetMembershipId,
            command.Reason,
            expectedVersion,
            stepUpToken,
            grant: false,
            cancellationToken);
    }

    public ValueTask<Result<VersionedResult<AdminRoleChangeRequestSummary>>> ProposeAdministratorRoleChangeAsync(
        ProposeAdministratorRoleChangeCommand command,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<string> reason = MemberAdministrationSupport.ValidateReason(command.Reason);
        if (reason.IsFailure)
        {
            return ValueTask.FromResult(
                Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(reason.Error));
        }

        return unitOfWork.ExecuteAsync(
            token => ProposeAdministratorRoleChangeCoreAsync(
                command.TargetMembershipId,
                command.Action,
                reason.Value,
                expectedVersion,
                stepUpToken,
                token),
            cancellationToken);
    }

    public ValueTask<Result<VersionedResult<AdminRoleChangeRequestSummary>>> ApproveAdministratorRoleChangeAsync(
        ApproveAdministratorRoleChangeCommand command,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<string> reason = MemberAdministrationSupport.ValidateReason(command.Reason);
        if (reason.IsFailure)
        {
            return ValueTask.FromResult(
                Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(reason.Error));
        }

        return unitOfWork.ExecuteAsync(
            token => ApproveAdministratorRoleChangeCoreAsync(
                command.RequestId,
                reason.Value,
                expectedVersion,
                stepUpToken,
                token),
            cancellationToken);
    }

    public ValueTask<Result<VersionedResult<AdminMemberSummary>>> DisableMembershipAsync(
        DisableMembershipCommand command,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<string> reason = MemberAdministrationSupport.ValidateReason(command.Reason);
        if (reason.IsFailure)
        {
            return ValueTask.FromResult(
                Result.Failure<VersionedResult<AdminMemberSummary>>(reason.Error));
        }

        return unitOfWork.ExecuteAsync(
            token => DisableMembershipCoreAsync(
                command.TargetMembershipId,
                reason.Value,
                expectedVersion,
                stepUpToken,
                token),
            cancellationToken);
    }

    private async ValueTask<Result<IssuedMembershipInvitation>> IssueInvitationCoreAsync(
        string email,
        int expiresInHours,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await membershipRepository.ResolveActiveActorAsync(
            currentActor.UserId,
            currentActor.MembershipId,
            currentActor.OrganizationId,
            cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<IssuedMembershipInvitation>(actor.Error);
        }

        Result<ActiveMembershipContext> authorized =
            MemberAdministrationSupport.EnsureAdministrator(actor.Value);
        if (authorized.IsFailure)
        {
            return Result.Failure<IssuedMembershipInvitation>(authorized.Error);
        }

        Result stepUp = await stepUpVerifier.ConsumeAsync(
            currentActor.UserId,
            stepUpToken,
            MemberAdministrationStepUpPurposes.InvitationIssue,
            cancellationToken);
        if (stepUp.IsFailure)
        {
            return Result.Failure<IssuedMembershipInvitation>(stepUp.Error);
        }

        DateTimeOffset issuedAt = clock.UtcNow;
        DateTimeOffset expiresAt = issuedAt.AddHours(expiresInHours);
        MembershipId membershipId = MembershipId.New();
        string invitationToken = InvitationTokenCodec.Create(membershipId);

        MembershipAdministrationPersistenceEffects effects = new(
            [],
            [
                MemberAdministrationSupport.CreateAuditEntry(
                    authorized.Value.OrganizationId,
                    authorized.Value.MembershipId,
                    "admin_invitation_issued",
                    "membership",
                    membershipId.ToString(),
                    "invitation issuance",
                    MembershipAdministrationPurpose,
                    beforeState: null,
                    afterState: MemberAdministrationSupport.Serialize(
                        new MembershipAuditState(
                            membershipId.ToString(),
                            MembershipStatus.Invited.ToString(),
                            Array.Empty<string>(),
                            EligibleAsNamedParticipant: true)),
                    issuedAt),
            ],
            []);

        Result persisted = await membershipRepository.IssueInvitationAsync(
            new IssueMembershipInvitationPersistenceRequest(
                UserId.New(),
                membershipId,
                authorized.Value.OrganizationId,
                authorized.Value.MembershipId,
                email,
                invitationToken,
                PendingInviteDisplayName,
                eligibleAsNamedParticipant: true,
                issuedAt,
                expiresAt,
                effects),
            cancellationToken);
        if (persisted.IsFailure)
        {
            return Result.Failure<IssuedMembershipInvitation>(persisted.Error);
        }

        return Result.Success(
            new IssuedMembershipInvitation(
                membershipId,
                invitationToken,
                expiresAt));
    }

    private ValueTask<Result<long>> ExecuteFoodInchargeChangeAsync(
        MembershipId targetMembershipId,
        string reasonText,
        long expectedVersion,
        StepUpToken stepUpToken,
        bool grant,
        CancellationToken cancellationToken)
    {
        Result<string> reason = MemberAdministrationSupport.ValidateReason(reasonText);
        if (reason.IsFailure)
        {
            return ValueTask.FromResult(Result.Failure<long>(reason.Error));
        }

        return unitOfWork.ExecuteAsync(
            token => ExecuteFoodInchargeChangeCoreAsync(
                targetMembershipId,
                reason.Value,
                expectedVersion,
                stepUpToken,
                grant,
                token),
            cancellationToken);
    }

    private async ValueTask<Result<long>> ExecuteFoodInchargeChangeCoreAsync(
        MembershipId targetMembershipId,
        string reason,
        long expectedVersion,
        StepUpToken stepUpToken,
        bool grant,
        CancellationToken cancellationToken)
    {
        Result<GovernanceMutationContext> context = await LoadGovernanceMutationContextAsync(
            expectedVersion,
            cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<long>(context.Error);
        }

        Result<Membership> target = MemberAdministrationSupport.FindMembership(
            context.Value.Governance,
            targetMembershipId);
        if (target.IsFailure)
        {
            return Result.Failure<long>(target.Error);
        }

        string beforeState = MemberAdministrationSupport.Serialize(
            MemberAdministrationSupport.ToAuditState(target.Value));
        Result result = grant
            ? context.Value.Governance.AssignFoodIncharge(
                    context.Value.Actor,
                    target.Value,
                    context.Value.OccurredAt)
                .ToResult()
            : context.Value.Governance.RevokeFoodIncharge(
                    context.Value.Actor,
                    target.Value,
                    context.Value.OccurredAt)
                .ToResult();
        if (result.IsFailure)
        {
            return Result.Failure<long>(result.Error);
        }

        Result stepUp = await stepUpVerifier.ConsumeAsync(
            currentActor.UserId,
            stepUpToken,
            MemberAdministrationStepUpPurposes.GovernanceAssign,
            cancellationToken);
        if (stepUp.IsFailure)
        {
            return Result.Failure<long>(stepUp.Error);
        }

        Membership updatedTarget = context.Value.Governance.Memberships.Single(
            membership => membership.Id == targetMembershipId);
        string action = grant ? "food_incharge_assigned" : "food_incharge_revoked";
        Result<NotificationCreated> notification = MemberAdministrationSupport.CreateAccountRoleNotification(
            context.Value.Governance.OrganizationId,
            updatedTarget.Id,
            updatedTarget.Id.Value,
            "Role updated",
            grant
                ? "Your Food Incharge access was granted."
                : "Your Food Incharge access was revoked.",
            context.Value.OccurredAt);
        if (notification.IsFailure)
        {
            return Result.Failure<long>(notification.Error);
        }

        Result saved = await membershipRepository.SaveGovernanceAsync(
            context.Value.Governance,
            currentActor.MembershipId,
            context.Value.OccurredAt,
            MemberAdministrationSupport.CreateEffects(
                [notification.Value],
                [
                    MemberAdministrationSupport.CreateAuditEntry(
                        context.Value.Governance.OrganizationId,
                        currentActor.MembershipId,
                        action,
                        "membership",
                        targetMembershipId.ToString(),
                        reason,
                        GovernancePurpose,
                        beforeState,
                        MemberAdministrationSupport.Serialize(
                            MemberAdministrationSupport.ToAuditState(updatedTarget)),
                        context.Value.OccurredAt),
                ]),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<long>(saved.Error);
        }

        return Result.Success(context.Value.Governance.Version);
    }

    private async ValueTask<Result<VersionedResult<AdminRoleChangeRequestSummary>>> ProposeAdministratorRoleChangeCoreAsync(
        MembershipId targetMembershipId,
        AdministratorRoleChangeAction action,
        string reason,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken)
    {
        Result<GovernanceMutationContext> context = await LoadGovernanceMutationContextAsync(
            expectedVersion,
            cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(context.Error);
        }

        Result<Membership> target = MemberAdministrationSupport.FindMembership(
            context.Value.Governance,
            targetMembershipId);
        if (target.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(target.Error);
        }

        Result<RoleChangeRequest> proposed = context.Value.Governance.ProposeAdministratorRoleChange(
            RoleChangeRequestId.New(),
            action,
            context.Value.Actor,
            target.Value,
            reason,
            context.Value.OccurredAt);
        if (proposed.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(proposed.Error);
        }

        Result stepUp = await stepUpVerifier.ConsumeAsync(
            currentActor.UserId,
            stepUpToken,
            MemberAdministrationStepUpPurposes.GovernanceAssign,
            cancellationToken);
        if (stepUp.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(stepUp.Error);
        }

        List<NotificationCreated> notifications = [];
        foreach (Membership administrator in context.Value.Governance.Memberships.Where(
                     membership => membership.Status == MembershipStatus.Active
                         && membership.HasRole(OrganizationRole.Admin)
                         && membership.Id != currentActor.MembershipId))
        {
            Result<NotificationCreated> notification =
                MemberAdministrationSupport.CreateAccountRoleNotification(
                    context.Value.Governance.OrganizationId,
                    administrator.Id,
                    targetMembershipId.Value,
                    "Administrator approval needed",
                    "An administrator role change request requires approval.",
                    context.Value.OccurredAt);
            if (notification.IsFailure)
            {
                return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(notification.Error);
            }

            notifications.Add(notification.Value);
        }

        Result saved = await membershipRepository.SaveGovernanceAsync(
            context.Value.Governance,
            currentActor.MembershipId,
            context.Value.OccurredAt,
            MemberAdministrationSupport.CreateEffects(
                notifications,
                [
                    MemberAdministrationSupport.CreateAuditEntry(
                        context.Value.Governance.OrganizationId,
                        currentActor.MembershipId,
                        "admin_role_request_proposed",
                        "role_change_request",
                        proposed.Value.Id.ToString(),
                        reason,
                        GovernancePurpose,
                        beforeState: null,
                        afterState: MemberAdministrationSupport.Serialize(
                            MemberAdministrationSupport.ToAuditState(proposed.Value)),
                        context.Value.OccurredAt),
                ]),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(saved.Error);
        }

        return Result.Success(
            new VersionedResult<AdminRoleChangeRequestSummary>(
                MemberAdministrationSupport.ToSummary(proposed.Value),
                context.Value.Governance.Version));
    }

    private async ValueTask<Result<VersionedResult<AdminRoleChangeRequestSummary>>> ApproveAdministratorRoleChangeCoreAsync(
        RoleChangeRequestId requestId,
        string reason,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken)
    {
        Result<GovernanceMutationContext> context = await LoadGovernanceMutationContextAsync(
            expectedVersion,
            cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(context.Error);
        }

        Result<RoleChangeRequest> request = MemberAdministrationSupport.FindRoleChangeRequest(
            context.Value.Governance,
            requestId);
        if (request.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(request.Error);
        }

        Result<Membership> proposer = MemberAdministrationSupport.FindMembership(
            context.Value.Governance,
            request.Value.ProposerMembershipId);
        if (proposer.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(proposer.Error);
        }

        Result<Membership> target = MemberAdministrationSupport.FindMembership(
            context.Value.Governance,
            request.Value.TargetMembershipId);
        if (target.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(target.Error);
        }

        string beforeState = MemberAdministrationSupport.Serialize(
            new
            {
                request = MemberAdministrationSupport.ToAuditState(request.Value),
                target = MemberAdministrationSupport.ToAuditState(target.Value),
            });
        Result<AdministratorRoleChangeApproved> approved =
            context.Value.Governance.ApproveAdministratorRoleChange(
                request.Value,
                proposer.Value,
                context.Value.Actor,
                target.Value,
                context.Value.OccurredAt);
        if (approved.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(approved.Error);
        }

        Result stepUp = await stepUpVerifier.ConsumeAsync(
            currentActor.UserId,
            stepUpToken,
            MemberAdministrationStepUpPurposes.GovernanceApprove,
            cancellationToken);
        if (stepUp.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(stepUp.Error);
        }

        RoleChangeRequest updatedRequest = context.Value.Governance.RoleChangeRequests.Single(
            candidate => candidate.Id == requestId);
        Membership updatedTarget = context.Value.Governance.Memberships.Single(
            membership => membership.Id == target.Value.Id);

        List<NotificationCreated> notifications = [];
        foreach (MembershipId recipientId in new[] { proposer.Value.Id, updatedTarget.Id }.Distinct())
        {
            Result<NotificationCreated> notification =
                MemberAdministrationSupport.CreateAccountRoleNotification(
                    context.Value.Governance.OrganizationId,
                    recipientId,
                    updatedTarget.Id.Value,
                    "Administrator role updated",
                    updatedRequest.Action == AdministratorRoleChangeAction.Grant
                        ? "An administrator role change request was approved."
                        : "An administrator revocation request was approved.",
                    context.Value.OccurredAt);
            if (notification.IsFailure)
            {
                return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(notification.Error);
            }

            notifications.Add(notification.Value);
        }

        Result saved = await membershipRepository.SaveGovernanceAsync(
            context.Value.Governance,
            currentActor.MembershipId,
            context.Value.OccurredAt,
            MemberAdministrationSupport.CreateEffects(
                notifications,
                [
                    MemberAdministrationSupport.CreateAuditEntry(
                        context.Value.Governance.OrganizationId,
                        currentActor.MembershipId,
                        "admin_role_request_approved",
                        "role_change_request",
                        requestId.ToString(),
                        reason,
                        GovernancePurpose,
                        beforeState,
                        MemberAdministrationSupport.Serialize(
                            new
                            {
                                request = MemberAdministrationSupport.ToAuditState(updatedRequest),
                                target = MemberAdministrationSupport.ToAuditState(updatedTarget),
                            }),
                        context.Value.OccurredAt),
                ]),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminRoleChangeRequestSummary>>(saved.Error);
        }

        return Result.Success(
            new VersionedResult<AdminRoleChangeRequestSummary>(
                MemberAdministrationSupport.ToSummary(updatedRequest),
                context.Value.Governance.Version));
    }

    private async ValueTask<Result<VersionedResult<AdminMemberSummary>>> DisableMembershipCoreAsync(
        MembershipId targetMembershipId,
        string reason,
        long expectedVersion,
        StepUpToken stepUpToken,
        CancellationToken cancellationToken)
    {
        Result<GovernanceMutationContext> context = await LoadGovernanceMutationContextAsync(
            expectedVersion,
            cancellationToken);
        if (context.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminMemberSummary>>(context.Error);
        }

        Result<Membership> target = MemberAdministrationSupport.FindMembership(
            context.Value.Governance,
            targetMembershipId);
        if (target.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminMemberSummary>>(target.Error);
        }

        string beforeState = MemberAdministrationSupport.Serialize(
            MemberAdministrationSupport.ToAuditState(target.Value));
        Result<MembershipDisabled> disabled = context.Value.Governance.DisableMembership(
            context.Value.Actor,
            target.Value,
            context.Value.OccurredAt);
        if (disabled.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminMemberSummary>>(disabled.Error);
        }

        Result stepUp = await stepUpVerifier.ConsumeAsync(
            currentActor.UserId,
            stepUpToken,
            MemberAdministrationStepUpPurposes.MembershipDisable,
            cancellationToken);
        if (stepUp.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminMemberSummary>>(stepUp.Error);
        }

        Membership updatedTarget = context.Value.Governance.Memberships.Single(
            membership => membership.Id == targetMembershipId);
        Result saved = await membershipRepository.SaveGovernanceAsync(
            context.Value.Governance,
            currentActor.MembershipId,
            context.Value.OccurredAt,
            MemberAdministrationSupport.CreateEffects(
                [],
                [
                    MemberAdministrationSupport.CreateAuditEntry(
                        context.Value.Governance.OrganizationId,
                        currentActor.MembershipId,
                        "membership_disabled",
                        "membership",
                        targetMembershipId.ToString(),
                        reason,
                        GovernancePurpose,
                        beforeState,
                        MemberAdministrationSupport.Serialize(
                            MemberAdministrationSupport.ToAuditState(updatedTarget)),
                        context.Value.OccurredAt),
                ]),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<VersionedResult<AdminMemberSummary>>(saved.Error);
        }

        return Result.Success(
            new VersionedResult<AdminMemberSummary>(
                MemberAdministrationSupport.ToSummary(
                    updatedTarget,
                    Array.Empty<AdminRoleChangeRequestSummary>()),
                context.Value.Governance.Version));
    }

    private async ValueTask<Result<GovernanceMutationContext>> LoadGovernanceMutationContextAsync(
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        Result<OrganizationAccountGovernance> governance = await membershipRepository.GetGovernanceAsync(
            currentActor.OrganizationId,
            cancellationToken);
        if (governance.IsFailure)
        {
            return Result.Failure<GovernanceMutationContext>(governance.Error);
        }

        Result<Membership> actor = MemberAdministrationSupport.ResolveAdministrator(
            governance.Value,
            currentActor);
        if (actor.IsFailure)
        {
            return Result.Failure<GovernanceMutationContext>(actor.Error);
        }

        Result expected = MemberAdministrationSupport.EnsureExpectedVersion(
            expectedVersion,
            governance.Value);
        if (expected.IsFailure)
        {
            return Result.Failure<GovernanceMutationContext>(expected.Error);
        }

        return Result.Success(
            new GovernanceMutationContext(
                governance.Value,
                actor.Value,
                clock.UtcNow));
    }

    private sealed record GovernanceMutationContext(
        OrganizationAccountGovernance Governance,
        Membership Actor,
        DateTimeOffset OccurredAt);
}

internal static class MemberAdministrationResultExtensions
{
    public static Result ToResult<T>(this Result<T> result) =>
        result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
}
