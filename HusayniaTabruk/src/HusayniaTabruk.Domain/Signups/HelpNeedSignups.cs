using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;

namespace HusayniaTabruk.Domain.Signups;

public sealed class HelpNeedSignups
{
    private readonly List<Signup> signups;
    private readonly DateTimeOffset endsAt;
    private readonly DateTimeOffset cancellationDeadlineAt;
    private readonly ServiceDateStatus serviceDateStatus;
    private readonly HelpNeedStatus helpNeedStatus;
    private readonly int? capacity;

    private HelpNeedSignups(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        HelpNeedId helpNeedId,
        long signupVersion,
        long waitlistOrderHighWater,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt,
        ServiceDateStatus serviceDateStatus,
        HelpNeedStatus helpNeedStatus,
        int? capacity,
        IReadOnlyCollection<Signup> signups)
    {
        OrganizationId = organizationId;
        ServiceDateId = serviceDateId;
        HelpNeedId = helpNeedId;
        OriginalVersion = signupVersion;
        Version = signupVersion;
        WaitlistOrderHighWater = waitlistOrderHighWater;
        this.endsAt = endsAt;
        this.cancellationDeadlineAt = cancellationDeadlineAt;
        this.serviceDateStatus = serviceDateStatus;
        this.helpNeedStatus = helpNeedStatus;
        this.capacity = capacity;
        this.signups = signups.Select(signup => signup.DeepCopy()).ToList();
    }

    public OrganizationId OrganizationId { get; }
    public ServiceDateId ServiceDateId { get; }
    public HelpNeedId HelpNeedId { get; }
    public long OriginalVersion { get; }
    public long Version { get; private set; }
    public long WaitlistOrderHighWater { get; private set; }
    public IReadOnlyCollection<Signup> Signups =>
        Array.AsReadOnly(signups.Select(signup => signup.DeepCopy()).ToArray());

    internal static class PersistenceFactory
    {
        internal static Result<HelpNeedSignups> Rehydrate(
            ServiceDate serviceDate,
            HelpNeed helpNeed,
            long signupVersion,
            long waitlistOrderHighWater,
            IReadOnlyCollection<Signup> signups)
        {
            ArgumentNullException.ThrowIfNull(serviceDate);
            ArgumentNullException.ThrowIfNull(helpNeed);
            ArgumentNullException.ThrowIfNull(signups);

            if (signupVersion < 0)
            {
                return InvalidState("The signup aggregate version cannot be negative.");
            }

            if (waitlistOrderHighWater < 0)
            {
                return InvalidState("The waitlist order high-water value cannot be negative.");
            }

            HelpNeed? representedNeed = serviceDate.HelpNeeds
                .SingleOrDefault(candidate => candidate.Id == helpNeed.Id);
            if (representedNeed is null
                || helpNeed.ServiceDateId != serviceDate.Id
                || representedNeed.ServiceDateId != serviceDate.Id
                || representedNeed.Category != helpNeed.Category
                || representedNeed.Instructions != helpNeed.Instructions
                || representedNeed.Capacity != helpNeed.Capacity
                || representedNeed.Status != helpNeed.Status
                || representedNeed.Version != helpNeed.Version)
            {
                return InvalidState(
                    "The help need must exactly match the canonical help need represented by the service date.");
            }

            if (signups.Any(signup => signup is null)
                || signups.Any(signup => !signup.HasValidPersistedState())
                || signups.Any(signup =>
                    signup.OrganizationId != serviceDate.OrganizationId
                    || signup.ServiceDateId != serviceDate.Id
                    || signup.HelpNeedId != helpNeed.Id)
                || signups.Select(signup => signup.Id).Distinct().Count() != signups.Count)
            {
                return InvalidState(
                    "Every signup must be valid, uniquely identified, and owned by the aggregate context.");
            }

            Signup[] activeSignups = signups
                .Where(signup => IsActive(signup.Status))
                .ToArray();
            if (activeSignups
                    .Select(signup => signup.PrimaryMembershipId)
                    .Distinct()
                    .Count() != activeSignups.Length)
            {
                return InvalidState(
                    "A primary membership may have at most one active signup for a help need.");
            }

            Signup[] waitlistedSignups = signups
                .Where(signup => signup.Status == SignupStatus.Waitlisted)
                .ToArray();
            if (waitlistedSignups
                    .Select(signup => signup.WaitlistOrder!.Value)
                    .Distinct()
                    .Count() != waitlistedSignups.Length)
            {
                return InvalidState("Waitlist orders must be unique within a help need.");
            }

            if (waitlistedSignups.Any(
                    signup => signup.WaitlistOrder!.Value > waitlistOrderHighWater))
            {
                return InvalidState(
                    "Every waitlist order must be at or below the persisted high-water value.");
            }

            if (helpNeed.Capacity.HasValue
                && ApprovedParticipantCount(signups) > helpNeed.Capacity.Value)
            {
                return InvalidState("The persisted signup set exceeds the help need capacity.");
            }

            return Result.Success(
                new HelpNeedSignups(
                    serviceDate.OrganizationId,
                    serviceDate.Id,
                    helpNeed.Id,
                    signupVersion,
                    waitlistOrderHighWater,
                    serviceDate.EndsAt,
                    serviceDate.CancellationDeadlineAt,
                    serviceDate.Status,
                    helpNeed.Status,
                    helpNeed.Capacity,
                    signups));
        }
    }

    public Result<SignupSubmitted> Submit(
        SignupId signupId,
        Membership primaryMembership,
        SignupKind kind,
        IReadOnlyCollection<Membership> memberParticipants,
        int unnamedParticipantCount,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(primaryMembership);
        ArgumentNullException.ThrowIfNull(memberParticipants);
        EnsureUtc(now);
        signupId.EnsureValid();

        Result rootVersion = ValidateRootVersion();
        if (rootVersion.IsFailure)
        {
            return Result.Failure<SignupSubmitted>(rootVersion.Error);
        }

        Result window = ValidateOpenWindow(now);
        if (window.IsFailure)
        {
            return Result.Failure<SignupSubmitted>(window.Error);
        }

        if (signups.Any(signup => signup.Id == signupId)
            || signups.Any(signup =>
                signup.PrimaryMembershipId == primaryMembership.Id
                && IsActive(signup.Status)))
        {
            return Result.Failure<SignupSubmitted>(
                SignupErrorCodes.Conflict(
                    ErrorCodes.SignupDuplicate,
                    "The signup ID or primary membership already has an active signup for this help need."));
        }

        Result<Signup> submitted = Signup.Submit(
            signupId,
            OrganizationId,
            ServiceDateId,
            HelpNeedId,
            primaryMembership,
            kind,
            memberParticipants,
            unnamedParticipantCount,
            now);
        if (submitted.IsFailure)
        {
            return Result.Failure<SignupSubmitted>(submitted.Error);
        }

        Signup signup = submitted.Value;
        signups.Add(signup);
        Version++;
        return Result.Success(
            new SignupSubmitted(
                signup.Id,
                HelpNeedId,
                signup.PrimaryMembershipId,
                signup.TotalParticipantCount,
                now));
    }

    public Result<SignupTransitioned> Approve(SignupId signupId, DateTimeOffset now)
    {
        Result<Signup> prepared = PrepareTransition(signupId, now, requireOpenWindow: true);
        if (prepared.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(prepared.Error);
        }

        Signup signup = prepared.Value;
        if (signup.Status != SignupStatus.Pending)
        {
            return InvalidTransition("Only a pending signup may be approved.");
        }

        Result available = ValidateCapacity(signup);
        if (available.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(available.Error);
        }

        return CompleteTransition(signup.Approve(now));
    }

    public Result<SignupTransitioned> Decline(SignupId signupId, DateTimeOffset now)
    {
        Result<Signup> prepared = PrepareTransition(signupId, now, requireOpenWindow: true);
        if (prepared.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(prepared.Error);
        }

        Signup signup = prepared.Value;
        if (signup.Status is not (SignupStatus.Pending or SignupStatus.Waitlisted))
        {
            return InvalidTransition("Only a pending or waitlisted signup may be declined.");
        }

        return CompleteTransition(signup.Decline(now));
    }

    public Result<SignupTransitioned> Waitlist(SignupId signupId, DateTimeOffset now)
    {
        Result<Signup> prepared = PrepareTransition(signupId, now, requireOpenWindow: true);
        if (prepared.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(prepared.Error);
        }

        Signup signup = prepared.Value;
        if (signup.Status != SignupStatus.Pending)
        {
            return InvalidTransition("Only a pending signup may be waitlisted.");
        }

        Result<long> nextOrder = NextWaitlistOrder();
        if (nextOrder.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(nextOrder.Error);
        }

        return CompleteWaitlistTransition(
            signup.Waitlist(nextOrder.Value, now),
            nextOrder.Value);
    }

    public Result<SignupTransitioned> Withdraw(SignupId signupId, DateTimeOffset now)
    {
        Result<Signup> prepared = PrepareTransition(signupId, now, requireOpenWindow: false);
        if (prepared.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(prepared.Error);
        }

        if (now >= cancellationDeadlineAt)
        {
            return Result.Failure<SignupTransitioned>(
                SignupErrorCodes.Conflict(
                    ErrorCodes.CancellationDeadlinePassed,
                    "The signup can no longer be withdrawn by its primary contact."));
        }

        Signup signup = prepared.Value;
        if (signup.Status is not (SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted))
        {
            return InvalidTransition("Only an active signup may be withdrawn.");
        }

        return CompleteTransition(signup.Withdraw(now));
    }

    public Result<SignupTransitioned> Override(
        SignupId signupId,
        SignupStatus targetStatus,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        if (!Enum.IsDefined(targetStatus))
        {
            return Result.Failure<SignupTransitioned>(
                SignupErrorCodes.Validation(
                    SignupErrorCodes.InvalidSignupInput,
                    "The override target state is undefined."));
        }

        Result<Signup> prepared = PrepareTransition(
            signupId,
            now,
            requireOpenWindow: true,
            timestampAlreadyValidated: true);
        if (prepared.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(prepared.Error);
        }

        Signup signup = prepared.Value;
        bool allowed =
            (signup.Status is SignupStatus.Pending or SignupStatus.Waitlisted
                && targetStatus is SignupStatus.Approved or SignupStatus.Declined)
            || (signup.Status == SignupStatus.Pending && targetStatus == SignupStatus.Waitlisted)
            || (signup.Status == SignupStatus.Approved && targetStatus == SignupStatus.Cancelled);
        if (!allowed)
        {
            return InvalidTransition($"A {signup.Status} signup cannot be overridden to {targetStatus}.");
        }

        if (targetStatus == SignupStatus.Approved)
        {
            Result available = ValidateCapacity(signup);
            if (available.IsFailure)
            {
                return Result.Failure<SignupTransitioned>(available.Error);
            }
        }

        long? waitlistOrder = null;
        if (targetStatus == SignupStatus.Waitlisted)
        {
            Result<long> nextOrder = NextWaitlistOrder();
            if (nextOrder.IsFailure)
            {
                return Result.Failure<SignupTransitioned>(nextOrder.Error);
            }

            waitlistOrder = nextOrder.Value;
        }

        Result<SignupTransitioned> transition =
            signup.Override(targetStatus, waitlistOrder, now);
        return waitlistOrder.HasValue
            ? CompleteWaitlistTransition(transition, waitlistOrder.Value)
            : CompleteTransition(transition);
    }

    public Result<SignupTransitioned> Cancel(SignupId signupId, DateTimeOffset now)
    {
        Result<Signup> prepared = PrepareTransition(signupId, now, requireOpenWindow: false);
        if (prepared.IsFailure)
        {
            return Result.Failure<SignupTransitioned>(prepared.Error);
        }

        Signup signup = prepared.Value;
        if (signup.Status is not (SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted))
        {
            return InvalidTransition("Only an active signup may be cancelled.");
        }

        return CompleteTransition(signup.Cancel(now));
    }

    public Result<WaitlistedSignupReassigned> Reassign(
        SignupId signupId,
        DateTimeOffset now)
    {
        Result<Signup> prepared = PrepareTransition(signupId, now, requireOpenWindow: true);
        if (prepared.IsFailure)
        {
            return Result.Failure<WaitlistedSignupReassigned>(prepared.Error);
        }

        Signup signup = prepared.Value;
        if (signup.Status != SignupStatus.Waitlisted)
        {
            return Result.Failure<WaitlistedSignupReassigned>(
                SignupErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "Only a waitlisted signup may be reassigned."));
        }

        Result available = ValidateCapacity(signup);
        if (available.IsFailure)
        {
            return Result.Failure<WaitlistedSignupReassigned>(available.Error);
        }

        Result<SignupTransitioned> approved = signup.ApproveFromWaitlist(now);
        if (approved.IsFailure)
        {
            return Result.Failure<WaitlistedSignupReassigned>(approved.Error);
        }

        Version++;
        return Result.Success(
            new WaitlistedSignupReassigned(
                HelpNeedId,
                signup.Id,
                signup.TotalParticipantCount,
                now));
    }

    public IReadOnlyCollection<Signup> OrderedWaitlist() =>
        Array.AsReadOnly(
            signups
                .Where(signup => signup.Status == SignupStatus.Waitlisted)
                .OrderBy(signup => signup.WaitlistOrder!.Value)
                .ThenBy(signup => signup.Id.ToString(), StringComparer.Ordinal)
                .Select(signup => signup.DeepCopy())
                .ToArray());

    private Result<Signup> PrepareTransition(
        SignupId signupId,
        DateTimeOffset now,
        bool requireOpenWindow,
        bool timestampAlreadyValidated = false)
    {
        if (!timestampAlreadyValidated)
        {
            EnsureUtc(now);
        }

        signupId.EnsureValid();
        Result<Signup> owned = ResolveOwned(signupId);
        if (owned.IsFailure)
        {
            return owned;
        }

        Result rootVersion = ValidateRootVersion();
        if (rootVersion.IsFailure)
        {
            return Result.Failure<Signup>(rootVersion.Error);
        }

        Result childTransition = owned.Value.ValidateTransition(now);
        if (childTransition.IsFailure)
        {
            return Result.Failure<Signup>(childTransition.Error);
        }

        if (requireOpenWindow)
        {
            Result window = ValidateOpenWindow(now);
            if (window.IsFailure)
            {
                return Result.Failure<Signup>(window.Error);
            }
        }

        return owned;
    }

    private Result<Signup> ResolveOwned(SignupId signupId)
    {
        Signup? signup = signups.SingleOrDefault(candidate => candidate.Id == signupId);
        return signup is null
            ? Result.Failure<Signup>(
                SignupErrorCodes.Validation(
                    SignupErrorCodes.SignupNotOwned,
                    "The signup is not owned by this help need aggregate."))
            : Result.Success(signup);
    }

    private Result ValidateRootVersion() =>
        Version == long.MaxValue
            ? Result.Failure(VersionExhausted("The signup aggregate version is exhausted."))
            : Result.Success();

    private Result ValidateOpenWindow(DateTimeOffset now) =>
        serviceDateStatus != ServiceDateStatus.Open
            || helpNeedStatus != HelpNeedStatus.Open
            || now >= endsAt
            ? Result.Failure(
                SignupErrorCodes.Conflict(
                    ErrorCodes.CategoryClosed,
                    "The service date and help need must be open before the service date end."))
            : Result.Success();

    private Result ValidateCapacity(Signup candidate)
    {
        if (!capacity.HasValue)
        {
            return Result.Success();
        }

        long approvedParticipants = signups
            .Where(signup => signup.Id != candidate.Id && signup.Status == SignupStatus.Approved)
            .Sum(signup => (long)signup.TotalParticipantCount);
        return approvedParticipants + candidate.TotalParticipantCount <= capacity.Value
            ? Result.Success()
            : Result.Failure(
                SignupErrorCodes.Conflict(
                    ErrorCodes.CapacityUnavailable,
                    "Approving the signup would exceed the help need capacity."));
    }

    private Result<long> NextWaitlistOrder()
    {
        return WaitlistOrderHighWater == long.MaxValue
            ? Result.Failure<long>(
                VersionExhausted("The waitlist order is exhausted."))
            : Result.Success(WaitlistOrderHighWater + 1);
    }

    private Result<SignupTransitioned> CompleteWaitlistTransition(
        Result<SignupTransitioned> transition,
        long allocatedOrder)
    {
        if (transition.IsFailure)
        {
            return transition;
        }

        WaitlistOrderHighWater = allocatedOrder;
        Version++;
        return transition;
    }

    private Result<SignupTransitioned> CompleteTransition(
        Result<SignupTransitioned> transition)
    {
        if (transition.IsFailure)
        {
            return transition;
        }

        Version++;
        return transition;
    }

    private static long ApprovedParticipantCount(IEnumerable<Signup> candidates) =>
        candidates
            .Where(signup => signup.Status == SignupStatus.Approved)
            .Sum(signup => (long)signup.TotalParticipantCount);

    private static bool IsActive(SignupStatus status) =>
        status is SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted;

    private static Result<SignupTransitioned> InvalidTransition(string message) =>
        Result.Failure<SignupTransitioned>(
            SignupErrorCodes.Conflict(ErrorCodes.InvalidTransition, message));

    private static Result<HelpNeedSignups> InvalidState(string message) =>
        Result.Failure<HelpNeedSignups>(
            SignupErrorCodes.Validation(
                SignupErrorCodes.InvalidSignupAggregateState,
                message));

    private static DomainError VersionExhausted(string message) =>
        SignupErrorCodes.Conflict(SignupErrorCodes.VersionExhausted, message);

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}

public sealed record WaitlistedSignupReassigned(
    HelpNeedId HelpNeedId,
    SignupId SignupId,
    int ParticipantCount,
    DateTimeOffset OccurredAt);
