using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Signups;

public sealed class Signup
{
    private readonly List<MembershipId> memberParticipantIds;

    private Signup(
        SignupId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        HelpNeedId helpNeedId,
        MembershipId primaryMembershipId,
        SignupKind kind,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        int unnamedParticipantCount,
        SignupStatus status,
        DateTimeOffset submittedAt,
        DateTimeOffset? lastTransitionAt,
        long? waitlistOrder,
        long version)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        ServiceDateId = serviceDateId.EnsureValid();
        HelpNeedId = helpNeedId.EnsureValid();
        PrimaryMembershipId = primaryMembershipId.EnsureValid();
        Kind = kind;
        this.memberParticipantIds = [.. memberParticipantIds];
        UnnamedParticipantCount = unnamedParticipantCount;
        Status = status;
        SubmittedAt = submittedAt;
        LastTransitionAt = lastTransitionAt;
        WaitlistOrder = waitlistOrder;
        Version = version;
    }

    public SignupId Id { get; }
    public OrganizationId OrganizationId { get; }
    public ServiceDateId ServiceDateId { get; }
    public HelpNeedId HelpNeedId { get; }
    public MembershipId PrimaryMembershipId { get; }
    public SignupKind Kind { get; }
    public IReadOnlyCollection<MembershipId> MemberParticipantIds =>
        Array.AsReadOnly(memberParticipantIds.ToArray());
    public int UnnamedParticipantCount { get; }
    public int TotalParticipantCount => 1 + memberParticipantIds.Count + UnnamedParticipantCount;
    public SignupStatus Status { get; private set; }
    public DateTimeOffset SubmittedAt { get; }
    public DateTimeOffset? LastTransitionAt { get; private set; }
    public long? WaitlistOrder { get; private set; }
    public long Version { get; private set; }

    internal static Result<Signup> Rehydrate(
        SignupId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        HelpNeedId helpNeedId,
        MembershipId primaryMembershipId,
        SignupKind kind,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        int unnamedParticipantCount,
        SignupStatus status,
        DateTimeOffset submittedAt,
        DateTimeOffset? lastTransitionAt,
        long? waitlistOrder,
        long version)
    {
        ArgumentNullException.ThrowIfNull(memberParticipantIds);
        EnsureUtc(submittedAt);
        if (lastTransitionAt.HasValue)
        {
            EnsureUtc(lastTransitionAt.Value);
        }

        id.EnsureValid();
        organizationId.EnsureValid();
        serviceDateId.EnsureValid();
        helpNeedId.EnsureValid();
        primaryMembershipId.EnsureValid();

        Result composition = ValidateComposition(kind, memberParticipantIds, unnamedParticipantCount);
        if (composition.IsFailure)
        {
            return Result.Failure<Signup>(composition.Error);
        }

        if (!Enum.IsDefined(status)
            || !HasReachableVersion(status, version)
            || memberParticipantIds.Contains(primaryMembershipId)
            || (lastTransitionAt.HasValue && lastTransitionAt.Value < submittedAt)
            || !HasValidMetadata(status, lastTransitionAt, waitlistOrder))
        {
            return InvalidInput("The persisted signup state is inconsistent.");
        }

        return Result.Success(
            new Signup(
                id,
                organizationId,
                serviceDateId,
                helpNeedId,
                primaryMembershipId,
                kind,
                memberParticipantIds,
                unnamedParticipantCount,
                status,
                submittedAt,
                lastTransitionAt,
                waitlistOrder,
                version));
    }

    internal static Result<Signup> Submit(
        SignupId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        HelpNeedId helpNeedId,
        Membership primaryMembership,
        SignupKind kind,
        IReadOnlyCollection<Membership> memberParticipants,
        int unnamedParticipantCount,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(primaryMembership);
        ArgumentNullException.ThrowIfNull(memberParticipants);
        EnsureUtc(now);
        id.EnsureValid();
        organizationId.EnsureValid();
        serviceDateId.EnsureValid();
        helpNeedId.EnsureValid();

        if (primaryMembership.Status != MembershipStatus.Active
            || primaryMembership.OrganizationId != organizationId)
        {
            return Result.Failure<Signup>(
                SignupErrorCodes.Conflict(
                    SignupErrorCodes.IneligibleParticipant,
                    "The primary contact must be an active membership in the service date organization."));
        }

        if (memberParticipants.Any(participant => participant is null)
            || memberParticipants.Any(participant =>
                participant.OrganizationId != organizationId
                || !participant.IsEligibleAsNamedParticipant)
            || memberParticipants.Any(participant => participant.Id == primaryMembership.Id))
        {
            return Result.Failure<Signup>(
                SignupErrorCodes.Conflict(
                    SignupErrorCodes.IneligibleParticipant,
                    "Referenced participants must be distinct active eligible memberships in the same organization."));
        }

        MembershipId[] participantIds = memberParticipants.Select(participant => participant.Id).ToArray();
        Result validation = ValidateComposition(kind, participantIds, unnamedParticipantCount);
        if (validation.IsFailure)
        {
            return Result.Failure<Signup>(validation.Error);
        }

        return Result.Success(
            new Signup(
                id,
                organizationId,
                serviceDateId,
                helpNeedId,
                primaryMembership.Id,
                kind,
                participantIds,
                unnamedParticipantCount,
                SignupStatus.Pending,
                now,
                lastTransitionAt: null,
                waitlistOrder: null,
                version: 0));
    }

    internal Result<SignupTransitioned> Approve(DateTimeOffset now) =>
        Status == SignupStatus.Pending
            ? TransitionTo(SignupStatus.Approved, now, waitlistOrder: null, isOverride: false)
            : InvalidTransition("Only a pending signup may be approved.");

    internal Result<SignupTransitioned> Decline(DateTimeOffset now) =>
        Status is SignupStatus.Pending or SignupStatus.Waitlisted
            ? TransitionTo(SignupStatus.Declined, now, waitlistOrder: null, isOverride: false)
            : InvalidTransition("Only a pending or waitlisted signup may be declined.");

    internal Result<SignupTransitioned> Waitlist(long waitlistOrder, DateTimeOffset now)
    {
        if (Status != SignupStatus.Pending)
        {
            return InvalidTransition("Only a pending signup may be waitlisted.");
        }

        if (waitlistOrder <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(waitlistOrder),
                "Waitlist order must be positive.");
        }

        return TransitionTo(SignupStatus.Waitlisted, now, waitlistOrder, isOverride: false);
    }

    internal Result<SignupTransitioned> Withdraw(DateTimeOffset now)
    {
        if (Status is not (SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted))
        {
            return InvalidTransition("Only an active signup may be withdrawn.");
        }

        SignupStatus target = Status == SignupStatus.Approved
            ? SignupStatus.Cancelled
            : SignupStatus.Withdrawn;
        return TransitionTo(target, now, waitlistOrder: null, isOverride: false);
    }

    internal Result<SignupTransitioned> Override(
        SignupStatus targetStatus,
        long? waitlistOrder,
        DateTimeOffset now)
    {
        if (!Enum.IsDefined(targetStatus))
        {
            return InvalidInput<SignupTransitioned>("The override target state is undefined.");
        }

        bool allowed =
            (Status is SignupStatus.Pending or SignupStatus.Waitlisted
                && targetStatus is SignupStatus.Approved or SignupStatus.Declined)
            || (Status == SignupStatus.Pending && targetStatus == SignupStatus.Waitlisted)
            || (Status == SignupStatus.Approved && targetStatus == SignupStatus.Cancelled);
        if (!allowed)
        {
            return InvalidTransition($"A {Status} signup cannot be overridden to {targetStatus}.");
        }

        if (targetStatus == SignupStatus.Waitlisted && waitlistOrder is null or <= 0)
        {
            throw new InvalidOperationException(
                "An override to waitlisted requires an aggregate-allocated positive waitlist order.");
        }

        return TransitionTo(targetStatus, now, waitlistOrder, isOverride: true);
    }

    internal Result<SignupTransitioned> Cancel(DateTimeOffset now) =>
        Status is SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted
            ? TransitionTo(SignupStatus.Cancelled, now, waitlistOrder: null, isOverride: true)
            : InvalidTransition("Only an active signup may be cancelled.");

    internal Result<SignupTransitioned> ApproveFromWaitlist(DateTimeOffset now) =>
        Status == SignupStatus.Waitlisted
            ? TransitionTo(SignupStatus.Approved, now, waitlistOrder: null, isOverride: false)
            : InvalidTransition("Only a waitlisted signup may be reassigned.");

    internal Result ValidateTransition(DateTimeOffset now)
    {
        EnsureUtc(now);

        if (Version == long.MaxValue)
        {
            return Result.Failure(VersionExhausted());
        }

        if (now < (LastTransitionAt ?? SubmittedAt))
        {
            return Result.Failure(
                SignupErrorCodes.Conflict(
                    SignupErrorCodes.SignupChronologyInvalid,
                    "A signup transition cannot predate submission or the previous transition."));
        }

        return Result.Success();
    }

    internal Signup DeepCopy() =>
        new(
            Id,
            OrganizationId,
            ServiceDateId,
            HelpNeedId,
            PrimaryMembershipId,
            Kind,
            memberParticipantIds,
            UnnamedParticipantCount,
            Status,
            SubmittedAt,
            LastTransitionAt,
            WaitlistOrder,
            Version);

    internal bool HasValidPersistedState() =>
        Id.IsValid
        && OrganizationId.IsValid
        && ServiceDateId.IsValid
        && HelpNeedId.IsValid
        && PrimaryMembershipId.IsValid
        && SubmittedAt.Offset == TimeSpan.Zero
        && (!LastTransitionAt.HasValue || LastTransitionAt.Value.Offset == TimeSpan.Zero)
        && ValidateComposition(Kind, memberParticipantIds, UnnamedParticipantCount).IsSuccess
        && !memberParticipantIds.Contains(PrimaryMembershipId)
        && (!LastTransitionAt.HasValue || LastTransitionAt.Value >= SubmittedAt)
        && Enum.IsDefined(Status)
        && HasReachableVersion(Status, Version)
        && HasValidMetadata(Status, LastTransitionAt, WaitlistOrder);

    private Result<SignupTransitioned> TransitionTo(
        SignupStatus targetStatus,
        DateTimeOffset now,
        long? waitlistOrder,
        bool isOverride)
    {
        Result validation = ValidateTransition(now);
        if (validation.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(validation.Error);
        }

        SignupStatus previousStatus = Status;
        Status = targetStatus;
        LastTransitionAt = now;
        WaitlistOrder = waitlistOrder;
        Version++;
        return Result.Success(
            new SignupTransitioned(Id, previousStatus, targetStatus, isOverride, now));
    }

    private static bool HasReachableVersion(SignupStatus status, long version) =>
        status switch
        {
            SignupStatus.Pending => version == 0,
            SignupStatus.Waitlisted => version == 1,
            SignupStatus.Approved => version is >= 1 and <= 2,
            SignupStatus.Declined => version is >= 1 and <= 2,
            SignupStatus.Withdrawn => version is >= 1 and <= 2,
            SignupStatus.Cancelled => version is >= 1 and <= 3,
            _ => false,
        };

    private static bool HasValidMetadata(
        SignupStatus status,
        DateTimeOffset? lastTransitionAt,
        long? waitlistOrder) =>
        status switch
        {
            SignupStatus.Pending => lastTransitionAt is null && waitlistOrder is null,
            SignupStatus.Waitlisted => lastTransitionAt is not null && waitlistOrder is > 0,
            _ => lastTransitionAt is not null && waitlistOrder is null,
        };

    private static Result ValidateComposition(
        SignupKind kind,
        IReadOnlyCollection<MembershipId> memberParticipantIds,
        int unnamedParticipantCount)
    {
        if (!Enum.IsDefined(kind)
            || unnamedParticipantCount < 0
            || memberParticipantIds.Count > ApplicationLimits.MaximumNamedParticipants
            || unnamedParticipantCount > ApplicationLimits.MaximumUnnamedParticipants
            || memberParticipantIds.Any(id => !id.IsValid)
            || memberParticipantIds.Distinct().Count() != memberParticipantIds.Count)
        {
            return Result.Failure(
                SignupErrorCodes.Validation(
                    SignupErrorCodes.InvalidSignupInput,
                    "Signup kind, participant references, or unnamed participant count is invalid."));
        }

        int total = 1 + memberParticipantIds.Count + unnamedParticipantCount;
        if (total > ApplicationLimits.MaximumTotalParticipants
            || (kind == SignupKind.Individual && total != 1)
            || (kind is SignupKind.Household or SignupKind.Team && total < 2))
        {
            return Result.Failure(
                SignupErrorCodes.Validation(
                    SignupErrorCodes.InvalidSignupInput,
                    "Participant composition does not match the signup kind or total limit."));
        }

        return Result.Success();
    }

    private static Result<SignupTransitioned> InvalidTransition(string message) =>
        Result.Failure<SignupTransitioned>(
            SignupErrorCodes.Conflict(ErrorCodes.InvalidTransition, message));

    private static Result<Signup> InvalidInput(string message) =>
        InvalidInput<Signup>(message);

    private static Result<T> InvalidInput<T>(string message) =>
        Result.Failure<T>(
            SignupErrorCodes.Validation(SignupErrorCodes.InvalidSignupInput, message));

    private static DomainError VersionExhausted() =>
        SignupErrorCodes.Conflict(
            SignupErrorCodes.VersionExhausted,
            "The signup version is exhausted.");

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}

public sealed record SignupSubmitted(
    SignupId SignupId,
    HelpNeedId HelpNeedId,
    MembershipId PrimaryMembershipId,
    int TotalParticipantCount,
    DateTimeOffset OccurredAt);

public sealed record SignupTransitioned(
    SignupId SignupId,
    SignupStatus PreviousStatus,
    SignupStatus CurrentStatus,
    bool IsOverride,
    DateTimeOffset OccurredAt);
