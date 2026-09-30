using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public enum SignupCancellationAuthority
{
    PrimaryContact = 1,
    ManagingFoodIncharge = 2,
}

public sealed record SignupCancellationContext(
    HelpNeedSignups Aggregate,
    SignupSummary Signup);

public sealed record SignupCancellationEffects(
    IReadOnlyCollection<Notification> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages);

public sealed record SignupCancellationWrite(
    SignupId SignupId,
    MembershipId ActorMembershipId,
    SignupCancellationAuthority Authority,
    SignupCancellationEffects Effects);

public partial interface ISignupRepository
{
    ValueTask<Result<SignupCancellationContext>> GetOwnedCancellationContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support member cancellation.");

    ValueTask<Result<SignupCancellationContext>> GetManagedCancellationContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support managed cancellation.");

    ValueTask<Result> SaveCancellationAsync(
        HelpNeedSignups aggregate,
        SignupCancellationWrite write,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support cancellation.");

    ValueTask<Result> SaveDateCancellationAsync(
        HelpNeedSignups aggregate,
        MembershipId actorMembershipId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support date cancellation.");
}
