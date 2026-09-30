using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public sealed record SignupDecisionContext(
    HelpNeedSignups Aggregate,
    SignupSummary Signup);

public sealed record SignupDecisionEffects(
    IReadOnlyCollection<Notification> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages);

public sealed record SignupDecisionWrite(
    SignupId SignupId,
    MembershipId ActorMembershipId,
    SignupDecisionEffects Effects);

public partial interface ISignupRepository
{
    ValueTask<Result<SignupDecisionContext>> GetDecisionContextAsync(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support signup decisions.");

    ValueTask<Result> SaveDecisionAsync(
        HelpNeedSignups aggregate,
        SignupDecisionWrite write,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This signup repository does not support signup decisions.");
}
