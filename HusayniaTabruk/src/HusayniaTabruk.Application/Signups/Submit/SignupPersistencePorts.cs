using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public sealed record SignupSubmissionContext(
    HelpNeedSignups Aggregate,
    Membership PrimaryMembership,
    IReadOnlyCollection<Membership> MemberParticipants,
    MembershipId ManagerMembershipId,
    HelpCategory Category);

public sealed record SignupPersistenceEffects(
    IReadOnlyCollection<Notification> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages);

public sealed record SignupSubmissionWrite(
    SignupId SignupId,
    string? Label,
    SignupPersistenceEffects Effects);

public partial interface ISignupRepository
{
    ValueTask<Result<HelpNeedSignups>> GetAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextAsync(
        OrganizationId organizationId,
        HelpNeedId helpNeedId,
        MembershipId primaryMembershipId,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        CancellationToken cancellationToken = default);

    ValueTask<Result> SaveSubmissionAsync(
        HelpNeedSignups aggregate,
        SignupSubmissionWrite write,
        CancellationToken cancellationToken = default);

    ValueTask<Result<SignupSummary>> GetOwnedAsync(
        OrganizationId organizationId,
        MembershipId primaryMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<SignupPage>> ListMineAsync(
        OrganizationId organizationId,
        MembershipId primaryMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default);
}
