using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Signups.Decisions;

public sealed class SignupDecisionService(
    IUnitOfWork unitOfWork,
    ISignupRepository signupRepository,
    IMembershipRepository membershipRepository,
    IIdempotencyStore idempotencyStore,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string ApproveOperation = "signup.approve";
    private const string DeclineOperation = "signup.decline";
    private const string WaitlistOperation = "signup.waitlist";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    public ValueTask<Result<SignupSummary>> ApproveAsync(
        ApproveSignupCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.SignupId,
            command.Reason,
            command.IdempotencyKey,
            expectedSignupVersion,
            ApproveOperation,
            reasonRequired: false,
            static (aggregate, signupId, now) => aggregate.Approve(signupId, now),
            cancellationToken);
    }

    public ValueTask<Result<SignupSummary>> DeclineAsync(
        DeclineSignupCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.SignupId,
            command.Reason,
            command.IdempotencyKey,
            expectedSignupVersion,
            DeclineOperation,
            reasonRequired: true,
            static (aggregate, signupId, now) => aggregate.Decline(signupId, now),
            cancellationToken);
    }

    public ValueTask<Result<SignupSummary>> WaitlistAsync(
        WaitlistSignupCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.SignupId,
            command.Reason,
            command.IdempotencyKey,
            expectedSignupVersion,
            WaitlistOperation,
            reasonRequired: false,
            static (aggregate, signupId, now) => aggregate.Waitlist(signupId, now),
            cancellationToken);
    }

    private ValueTask<Result<SignupSummary>> ExecuteAsync(
        SignupId signupId,
        string? reason,
        IdempotencyKey idempotencyKey,
        long expectedSignupVersion,
        string operation,
        bool reasonRequired,
        Func<HelpNeedSignups, SignupId, DateTimeOffset, Result<SignupTransitioned>> transition,
        CancellationToken cancellationToken)
    {
        signupId.EnsureValid();
        idempotencyKey.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(transition);

        return unitOfWork.ExecuteAsync(
            token => ExecuteCoreAsync(
                signupId,
                reason,
                idempotencyKey,
                expectedSignupVersion,
                operation,
                reasonRequired,
                transition,
                token),
            cancellationToken);
    }

    private async ValueTask<Result<SignupSummary>> ExecuteCoreAsync(
        SignupId signupId,
        string? reason,
        IdempotencyKey idempotencyKey,
        long expectedSignupVersion,
        string operation,
        bool reasonRequired,
        Func<HelpNeedSignups, SignupId, DateTimeOffset, Result<SignupTransitioned>> transition,
        CancellationToken cancellationToken)
    {
        Result<string?> normalizedReason = NormalizeReason(reason, reasonRequired);
        if (normalizedReason.IsFailure)
        {
            return Result.Failure<SignupSummary>(normalizedReason.Error);
        }

        if (expectedSignupVersion < 0)
        {
            return InvalidInput("The expected signup version must be nonnegative.");
        }

        Result<ActiveMembershipContext> actor =
            await membershipRepository.ResolveActiveActorAsync(
                currentActor.UserId,
                currentActor.MembershipId,
                currentActor.OrganizationId,
                cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<SignupSummary>(actor.Error);
        }

        RequestFingerprint fingerprint = Fingerprint(
            signupId,
            normalizedReason.Value,
            expectedSignupVersion);
        DateTimeOffset now = clock.UtcNow;
        IdempotencyCreateResult receipt = await idempotencyStore.TryCreateProcessingAsync(
            new IdempotencyCreateRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                idempotencyKey,
                operation,
                fingerprint,
                now,
                now.Add(IdempotencyLifetime)),
            cancellationToken);

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingProcessing)
        {
            return Result.Failure<SignupSummary>(
                DomainError.Conflict(
                    SignupApplicationErrorCodes.IdempotencyInProgress,
                    "The signup decision is already processing."));
        }

        if (receipt.Outcome is IdempotencyCreateOutcome.ExistingFailed
            or IdempotencyCreateOutcome.RequestMismatch
            or IdempotencyCreateOutcome.Expired)
        {
            return Result.Failure<SignupSummary>(
                DomainError.Conflict(
                    SignupApplicationErrorCodes.IdempotencyMismatch,
                    "The idempotency key cannot be used for this signup decision."));
        }

        Result<SignupDecisionContext> loaded =
            await signupRepository.GetDecisionContextAsync(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                signupId,
                cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<SignupSummary>(loaded.Error);
        }

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingCompleted)
        {
            if (!SignupId.TryParse(receipt.Receipt.ResultReference, out SignupId completedSignupId)
                || completedSignupId != signupId)
            {
                throw new InvalidOperationException(
                    "The completed signup decision receipt contains an invalid result reference.");
            }

            return Result.Success(loaded.Value.Signup);
        }

        if (receipt.Outcome != IdempotencyCreateOutcome.Created)
        {
            throw new InvalidOperationException(
                $"Unsupported signup decision idempotency outcome: {receipt.Outcome}.");
        }

        HelpNeedSignups aggregate = loaded.Value.Aggregate;
        if (aggregate.Version != expectedSignupVersion)
        {
            return Result.Failure<SignupSummary>(
                DomainError.PreconditionFailed(
                    ErrorCodes.StaleVersion,
                    "The signup aggregate has changed since it was read."));
        }

        DecisionState before = DecisionState.From(
            loaded.Value.Signup.Status,
            loaded.Value.Signup.Version,
            aggregate.Version,
            loaded.Value.Signup.WaitlistOrder);
        Result<SignupTransitioned> transitioned = transition(aggregate, signupId, now);
        if (transitioned.IsFailure)
        {
            return Result.Failure<SignupSummary>(transitioned.Error);
        }

        Signup changed = aggregate.Signups.Single(signup => signup.Id == signupId);
        SignupSummary summary = loaded.Value.Signup with
        {
            Status = changed.Status,
            LastTransitionAt = changed.LastTransitionAt,
            WaitlistOrder = changed.WaitlistOrder,
            Version = changed.Version,
            SignupVersion = aggregate.Version,
        };
        SignupDecisionEffects effects = CreateEffects(
            operation,
            normalizedReason.Value,
            idempotencyKey,
            currentActor.MembershipId,
            aggregate,
            loaded.Value.Signup.PrimaryContact.MembershipId,
            signupId,
            before,
            DecisionState.From(
                changed.Status,
                changed.Version,
                aggregate.Version,
                changed.WaitlistOrder),
            now);
        Result saved = await signupRepository.SaveDecisionAsync(
            aggregate,
            new SignupDecisionWrite(
                signupId,
                currentActor.MembershipId,
                effects),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<SignupSummary>(saved.Error);
        }

        IdempotencyTransitionResult completed = await idempotencyStore.TryCompleteAsync(
            new IdempotencyRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                idempotencyKey,
                operation,
                fingerprint),
            signupId.ToString(),
            cancellationToken);
        if (completed.Outcome != IdempotencyTransitionOutcome.Completed)
        {
            throw new InvalidOperationException(
                "The signup decision idempotency receipt could not be completed.");
        }

        return Result.Success(summary);
    }

    private static Result<string?> NormalizeReason(string? reason, bool required)
    {
        string? normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (required && normalized is null)
        {
            return InvalidInput<string?>("A nonblank decline reason is required.");
        }

        if (normalized is not null
            && (normalized.EnumerateRunes().Count()
                > ApplicationLimits.MaximumReasonUnicodeScalars
                || Encoding.UTF8.GetByteCount(normalized)
                > ApplicationLimits.MaximumReasonUtf8Bytes))
        {
            return Result.Failure<string?>(
                DomainError.PayloadTooLarge(
                    "The signup decision reason exceeds the permitted content size."));
        }

        return Result.Success(normalized);
    }

    private static RequestFingerprint Fingerprint(
        SignupId signupId,
        string? reason,
        long expectedSignupVersion)
    {
        string canonical = JsonSerializer.Serialize(
            new
            {
                signupId = signupId.ToString(),
                reason,
                expectedSignupVersion,
            });
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return RequestFingerprint.FromSha256(Convert.ToHexString(hash));
    }

    private static SignupDecisionEffects CreateEffects(
        string operation,
        string? reason,
        IdempotencyKey idempotencyKey,
        MembershipId actorMembershipId,
        HelpNeedSignups aggregate,
        MembershipId recipientMembershipId,
        SignupId signupId,
        DecisionState before,
        DecisionState after,
        DateTimeOffset now)
    {
        (string title, string body, string action, string defaultReason) = operation switch
        {
            ApproveOperation => (
                "Signup approved",
                "Your signup request was approved.",
                "signup.approved",
                "Signup approved by the managing Food Incharge."),
            DeclineOperation => (
                "Signup declined",
                "Your signup request was declined.",
                "signup.declined",
                string.Empty),
            WaitlistOperation => (
                "Signup waitlisted",
                "Your signup request was added to the waitlist.",
                "signup.waitlisted",
                "Signup waitlisted by the managing Food Incharge."),
            _ => throw new InvalidOperationException(
                $"Unsupported signup decision operation: {operation}."),
        };
        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            aggregate.OrganizationId,
            recipientMembershipId,
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            signupId.Value,
            title,
            body,
            now);
        if (notification.IsFailure)
        {
            throw new InvalidOperationException(
                "The privacy-safe signup decision notification could not be created.");
        }

        AuditEntry audit = new(
            AuditEventId.New(),
            aggregate.OrganizationId,
            actorMembershipId,
            action,
            "signup",
            signupId.ToString(),
            reason ?? defaultReason,
            "signup_decision",
            idempotencyKey.ToString(),
            JsonSerializer.Serialize(
                new
                {
                    status = before.Status,
                    childVersion = before.ChildVersion,
                    rootSignupVersion = before.RootSignupVersion,
                    waitlistOrder = before.WaitlistOrder,
                }),
            JsonSerializer.Serialize(
                new
                {
                    status = after.Status,
                    childVersion = after.ChildVersion,
                    rootSignupVersion = after.RootSignupVersion,
                    waitlistOrder = after.WaitlistOrder,
                }),
            now);
        OutboxMessage outbox = new(
            OutboxMessageId.New(),
            aggregate.OrganizationId,
            "notification.push_requested",
            JsonSerializer.Serialize(
                new
                {
                    notificationId = notification.Value.Notification.Id.ToString(),
                    recipientMembershipId = recipientMembershipId.ToString(),
                    resourceType = "signup",
                    resourceId = signupId.ToString(),
                }),
            now);
        return new SignupDecisionEffects(
            [notification.Value.Notification],
            [audit],
            [outbox]);
    }

    private static Result<SignupSummary> InvalidInput(string message) =>
        InvalidInput<SignupSummary>(message);

    private static Result<T> InvalidInput<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));

    private sealed record DecisionState(
        string Status,
        long ChildVersion,
        long RootSignupVersion,
        long? WaitlistOrder)
    {
        public static DecisionState From(
            SignupStatus status,
            long childVersion,
            long rootSignupVersion,
            long? waitlistOrder) =>
            new(
                status.ToString().ToLowerInvariant(),
                childVersion,
                rootSignupVersion,
                waitlistOrder);
    }
}
