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

namespace HusayniaTabruk.Application.Signups.Cancellation;

public sealed class SignupCancellationService(
    IUnitOfWork unitOfWork,
    ISignupRepository signupRepository,
    IMembershipRepository membershipRepository,
    IIdempotencyStore idempotencyStore,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string WithdrawOperation = "signup.withdraw";
    private const string OverrideOperation = "signup.cancellation_override";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    public ValueTask<Result<SignupSummary>> WithdrawAsync(
        WithdrawSignupCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.SignupId,
            targetStatus: null,
            reason: null,
            command.IdempotencyKey,
            expectedSignupVersion,
            WithdrawOperation,
            SignupCancellationAuthority.PrimaryContact,
            static (aggregate, signupId, _, now) => aggregate.Withdraw(signupId, now),
            cancellationToken);
    }

    public ValueTask<Result<SignupSummary>> OverrideAsync(
        OverrideSignupCancellationCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ExecuteAsync(
            command.SignupId,
            command.TargetStatus,
            command.Reason,
            command.IdempotencyKey,
            expectedSignupVersion,
            OverrideOperation,
            SignupCancellationAuthority.ManagingFoodIncharge,
            static (aggregate, signupId, targetStatus, now) =>
                aggregate.Override(signupId, targetStatus!.Value, now),
            cancellationToken);
    }

    private ValueTask<Result<SignupSummary>> ExecuteAsync(
        SignupId signupId,
        SignupStatus? targetStatus,
        string? reason,
        IdempotencyKey idempotencyKey,
        long expectedSignupVersion,
        string operation,
        SignupCancellationAuthority authority,
        Func<
            HelpNeedSignups,
            SignupId,
            SignupStatus?,
            DateTimeOffset,
            Result<SignupTransitioned>> transition,
        CancellationToken cancellationToken)
    {
        signupId.EnsureValid();
        idempotencyKey.EnsureValid();
        ArgumentNullException.ThrowIfNull(transition);
        return unitOfWork.ExecuteAsync(
            token => ExecuteCoreAsync(
                signupId,
                targetStatus,
                reason,
                idempotencyKey,
                expectedSignupVersion,
                operation,
                authority,
                transition,
                token),
            cancellationToken);
    }

    private async ValueTask<Result<SignupSummary>> ExecuteCoreAsync(
        SignupId signupId,
        SignupStatus? targetStatus,
        string? reason,
        IdempotencyKey idempotencyKey,
        long expectedSignupVersion,
        string operation,
        SignupCancellationAuthority authority,
        Func<
            HelpNeedSignups,
            SignupId,
            SignupStatus?,
            DateTimeOffset,
            Result<SignupTransitioned>> transition,
        CancellationToken cancellationToken)
    {
        Result<string?> normalizedReason = NormalizeReason(
            reason,
            required: authority == SignupCancellationAuthority.ManagingFoodIncharge);
        if (normalizedReason.IsFailure)
        {
            return Result.Failure<SignupSummary>(normalizedReason.Error);
        }

        if (targetStatus.HasValue
            && (!Enum.IsDefined(targetStatus.Value)
                || targetStatus.Value != SignupStatus.Cancelled))
        {
            return InvalidInput(
                "The cancellation override target state must be cancelled.");
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
            targetStatus,
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
                    "The signup cancellation is already processing."));
        }

        if (receipt.Outcome is IdempotencyCreateOutcome.ExistingFailed
            or IdempotencyCreateOutcome.RequestMismatch
            or IdempotencyCreateOutcome.Expired)
        {
            return Result.Failure<SignupSummary>(
                DomainError.Conflict(
                    SignupApplicationErrorCodes.IdempotencyMismatch,
                    "The idempotency key cannot be used for this signup cancellation."));
        }

        Result<SignupCancellationContext> loaded = await LoadContextAsync(
            authority,
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
                    "The completed signup cancellation receipt contains an invalid result reference.");
            }

            return Result.Success(loaded.Value.Signup);
        }

        if (receipt.Outcome != IdempotencyCreateOutcome.Created)
        {
            throw new InvalidOperationException(
                $"Unsupported signup cancellation idempotency outcome: {receipt.Outcome}.");
        }

        HelpNeedSignups aggregate = loaded.Value.Aggregate;
        if (aggregate.Version != expectedSignupVersion)
        {
            return Result.Failure<SignupSummary>(
                DomainError.PreconditionFailed(
                    ErrorCodes.StaleVersion,
                    "The signup aggregate has changed since it was read."));
        }

        SignupStatus beforeStatus = loaded.Value.Signup.Status;
        CancellationState before = CancellationState.From(
            loaded.Value.Signup.Status,
            loaded.Value.Signup.Version,
            aggregate.Version,
            loaded.Value.Signup.WaitlistOrder);
        Result<SignupTransitioned> transitioned =
            transition(aggregate, signupId, targetStatus, now);
        if (transitioned.IsFailure)
        {
            if (authority == SignupCancellationAuthority.PrimaryContact
                && transitioned.Error.Code
                    == ErrorCodes.CancellationDeadlinePassed)
            {
                return Result.Failure<SignupSummary>(
                    DomainError.Conflict(
                        ErrorCodes.CancellationDeadlinePassed,
                        "The cancellation deadline has passed. Contact the managing Food Incharge."));
            }

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
        SignupCancellationEffects effects = CreateEffects(
            authority,
            normalizedReason.Value,
            idempotencyKey,
            currentActor.MembershipId,
            aggregate,
            changed.PrimaryMembershipId,
            signupId,
            beforeStatus,
            changed.Status,
            before,
            CancellationState.From(
                changed.Status,
                changed.Version,
                aggregate.Version,
                changed.WaitlistOrder),
            now);
        Result saved = await signupRepository.SaveCancellationAsync(
            aggregate,
            new SignupCancellationWrite(
                signupId,
                currentActor.MembershipId,
                authority,
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
                "The signup cancellation idempotency receipt could not be completed.");
        }

        return Result.Success(summary);
    }

    private async ValueTask<Result<SignupCancellationContext>> LoadContextAsync(
        SignupCancellationAuthority authority,
        SignupId signupId,
        CancellationToken cancellationToken)
    {
        return authority == SignupCancellationAuthority.PrimaryContact
            ? await signupRepository.GetOwnedCancellationContextAsync(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                signupId,
                cancellationToken)
            : await signupRepository.GetManagedCancellationContextAsync(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                signupId,
                cancellationToken);
    }

    private static Result<string?> NormalizeReason(string? reason, bool required)
    {
        string? normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (required && normalized is null)
        {
            return InvalidInput<string?>(
                "A nonblank cancellation override reason is required.");
        }

        if (normalized is not null
            && (normalized.EnumerateRunes().Count()
                > ApplicationLimits.MaximumReasonUnicodeScalars
                || Encoding.UTF8.GetByteCount(normalized)
                > ApplicationLimits.MaximumReasonUtf8Bytes))
        {
            return Result.Failure<string?>(
                DomainError.PayloadTooLarge(
                    "The cancellation reason exceeds the permitted content size."));
        }

        return Result.Success(normalized);
    }

    private static RequestFingerprint Fingerprint(
        SignupId signupId,
        SignupStatus? targetStatus,
        string? reason,
        long expectedSignupVersion)
    {
        string canonical = JsonSerializer.Serialize(
            new
            {
                signupId = signupId.ToString(),
                targetStatus = targetStatus?.ToString().ToLowerInvariant(),
                reason,
                expectedSignupVersion,
            });
        return RequestFingerprint.FromSha256(
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static SignupCancellationEffects CreateEffects(
        SignupCancellationAuthority authority,
        string? reason,
        IdempotencyKey idempotencyKey,
        MembershipId actorMembershipId,
        HelpNeedSignups aggregate,
        MembershipId recipientMembershipId,
        SignupId signupId,
        SignupStatus beforeStatus,
        SignupStatus afterStatus,
        CancellationState before,
        CancellationState after,
        DateTimeOffset now)
    {
        bool overrideCancellation =
            authority == SignupCancellationAuthority.ManagingFoodIncharge;
        bool cancelled = afterStatus == SignupStatus.Cancelled;
        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            aggregate.OrganizationId,
            recipientMembershipId,
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            signupId.Value,
            cancelled ? "Signup cancelled" : "Signup withdrawn",
            overrideCancellation
                ? "Your signup was cancelled by the managing Food Incharge."
                : cancelled
                    ? "Your signup was cancelled."
                    : "Your signup was withdrawn.",
            now);
        if (notification.IsFailure)
        {
            throw new InvalidOperationException(
                "The privacy-safe signup cancellation notification could not be created.");
        }

        AuditEntry audit = new(
            AuditEventId.New(),
            aggregate.OrganizationId,
            actorMembershipId,
            cancelled ? "signup.cancelled" : "signup.withdrawn",
            "signup",
            signupId.ToString(),
            reason ?? "Signup withdrawn by its primary contact.",
            overrideCancellation ? "signup_cancellation_override" : "signup_cancellation",
            idempotencyKey.ToString(),
            SerializeState(before),
            SerializeState(after),
            now);
        List<OutboxMessage> outbox =
        [
            new(
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
                now),
        ];
        if (beforeStatus == SignupStatus.Approved)
        {
            outbox.Add(
                new OutboxMessage(
                    OutboxMessageId.New(),
                    aggregate.OrganizationId,
                    "thread.access_changed",
                    JsonSerializer.Serialize(
                        new
                        {
                            serviceDateId = aggregate.ServiceDateId.ToString(),
                            membershipId = recipientMembershipId.ToString(),
                            eligible = false,
                            signupId = signupId.ToString(),
                        }),
                    now));
        }

        return new SignupCancellationEffects(
            [notification.Value.Notification],
            [audit],
            outbox);
    }

    private static string SerializeState(CancellationState state) =>
        JsonSerializer.Serialize(
            new
            {
                status = state.Status,
                childVersion = state.ChildVersion,
                rootSignupVersion = state.RootSignupVersion,
                waitlistOrder = state.WaitlistOrder,
            });

    private static Result<SignupSummary> InvalidInput(string message) =>
        InvalidInput<SignupSummary>(message);

    private static Result<T> InvalidInput<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));

    private sealed record CancellationState(
        string Status,
        long ChildVersion,
        long RootSignupVersion,
        long? WaitlistOrder)
    {
        public static CancellationState From(
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
