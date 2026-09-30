using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public sealed record SignupReassignmentContext(
    HelpNeedSignups Aggregate,
    SignupSummary Signup);

public sealed record SignupReassignmentEffects(
    IReadOnlyCollection<Notification> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages);

public sealed record SignupReassignmentWrite(
    HelpNeedId HelpNeedId,
    SignupId SignupId,
    MembershipId ActorMembershipId,
    SignupReassignmentEffects Effects);

public partial interface ISignupRepository
{
    ValueTask<Result<SignupReassignmentContext>> GetReassignmentContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        HelpNeedId helpNeedId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support waitlist reassignment.");

    ValueTask<Result> SaveReassignmentAsync(
        HelpNeedSignups aggregate,
        SignupReassignmentWrite write,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support waitlist reassignment.");
}
