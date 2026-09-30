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

namespace HusayniaTabruk.Application.Signups.Reassignment;

public sealed class SignupReassignmentService(
    IUnitOfWork unitOfWork,
    ISignupRepository signupRepository,
    IMembershipRepository membershipRepository,
    IIdempotencyStore idempotencyStore,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string Operation = "signup.reassign";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    public ValueTask<Result<SignupSummary>> ReassignAsync(
        ReassignWaitlistedSignupCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.HelpNeedId.EnsureValid();
        command.SignupId.EnsureValid();
        command.IdempotencyKey.EnsureValid();
        return unitOfWork.ExecuteAsync(
            token => ExecuteCoreAsync(command, expectedSignupVersion, token),
            cancellationToken);
    }

    private async ValueTask<Result<SignupSummary>> ExecuteCoreAsync(
        ReassignWaitlistedSignupCommand command,
        long expectedSignupVersion,
        CancellationToken cancellationToken)
    {
        Result<string?> reason = NormalizeReason(command.Reason);
        if (reason.IsFailure)
        {
            return Result.Failure<SignupSummary>(reason.Error);
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
            command,
            reason.Value,
            expectedSignupVersion);
        DateTimeOffset now = clock.UtcNow;
        IdempotencyCreateResult receipt = await idempotencyStore.TryCreateProcessingAsync(
            new IdempotencyCreateRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.IdempotencyKey,
                Operation,
                fingerprint,
                now,
                now.Add(IdempotencyLifetime)),
            cancellationToken);
        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingProcessing)
        {
            return Conflict(
                SignupApplicationErrorCodes.IdempotencyInProgress,
                "The signup reassignment is already processing.");
        }

        if (receipt.Outcome is IdempotencyCreateOutcome.ExistingFailed
            or IdempotencyCreateOutcome.RequestMismatch
            or IdempotencyCreateOutcome.Expired)
        {
            return Conflict(
                SignupApplicationErrorCodes.IdempotencyMismatch,
                "The idempotency key cannot be used for this signup reassignment.");
        }

        Result<SignupReassignmentContext> loaded =
            await signupRepository.GetReassignmentContextAsync(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.HelpNeedId,
                command.SignupId,
                cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<SignupSummary>(loaded.Error);
        }

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingCompleted)
        {
            if (!SignupId.TryParse(receipt.Receipt.ResultReference, out SignupId completedSignupId)
                || completedSignupId != command.SignupId)
            {
                throw new InvalidOperationException(
                    "The completed signup reassignment receipt contains an invalid result reference.");
            }

            return Result.Success(loaded.Value.Signup);
        }

        if (receipt.Outcome != IdempotencyCreateOutcome.Created)
        {
            throw new InvalidOperationException(
                $"Unsupported signup reassignment idempotency outcome: {receipt.Outcome}.");
        }

        HelpNeedSignups aggregate = loaded.Value.Aggregate;
        if (aggregate.Version != expectedSignupVersion)
        {
            return Result.Failure<SignupSummary>(
                DomainError.PreconditionFailed(
                    ErrorCodes.StaleVersion,
                    "The signup aggregate has changed since it was read."));
        }

        ReassignmentState before = ReassignmentState.From(
            loaded.Value.Signup.Status,
            loaded.Value.Signup.Version,
            aggregate.Version,
            loaded.Value.Signup.WaitlistOrder);
        Result<WaitlistedSignupReassigned> reassigned =
            aggregate.Reassign(command.SignupId, now);
        if (reassigned.IsFailure)
        {
            return Result.Failure<SignupSummary>(reassigned.Error);
        }

        Signup changed = aggregate.Signups.Single(
            signup => signup.Id == command.SignupId);
        SignupSummary summary = loaded.Value.Signup with
        {
            Status = changed.Status,
            LastTransitionAt = changed.LastTransitionAt,
            WaitlistOrder = changed.WaitlistOrder,
            Version = changed.Version,
            SignupVersion = aggregate.Version,
        };
        SignupReassignmentEffects effects = CreateEffects(
            reason.Value,
            command.IdempotencyKey,
            currentActor.MembershipId,
            aggregate,
            changed.PrimaryMembershipId,
            command.SignupId,
            before,
            ReassignmentState.From(
                changed.Status,
                changed.Version,
                aggregate.Version,
                changed.WaitlistOrder),
            now);
        Result saved = await signupRepository.SaveReassignmentAsync(
            aggregate,
            new SignupReassignmentWrite(
                command.HelpNeedId,
                command.SignupId,
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
                command.IdempotencyKey,
                Operation,
                fingerprint),
            command.SignupId.ToString(),
            cancellationToken);
        if (completed.Outcome != IdempotencyTransitionOutcome.Completed)
        {
            throw new InvalidOperationException(
                "The signup reassignment idempotency receipt could not be completed.");
        }

        return Result.Success(summary);
    }

    private static Result<string?> NormalizeReason(string? reason)
    {
        string? normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalized is not null
            && (normalized.EnumerateRunes().Count()
                > ApplicationLimits.MaximumReasonUnicodeScalars
                || Encoding.UTF8.GetByteCount(normalized)
                > ApplicationLimits.MaximumReasonUtf8Bytes))
        {
            return Result.Failure<string?>(
                DomainError.PayloadTooLarge(
                    "The reassignment reason exceeds the permitted content size."));
        }

        return Result.Success(normalized);
    }

    private static RequestFingerprint Fingerprint(
        ReassignWaitlistedSignupCommand command,
        string? reason,
        long expectedSignupVersion)
    {
        string canonical = JsonSerializer.Serialize(
            new
            {
                helpNeedId = command.HelpNeedId.ToString(),
                signupId = command.SignupId.ToString(),
                reason,
                expectedSignupVersion,
            });
        return RequestFingerprint.FromSha256(
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static SignupReassignmentEffects CreateEffects(
        string? reason,
        IdempotencyKey idempotencyKey,
        MembershipId actorMembershipId,
        HelpNeedSignups aggregate,
        MembershipId recipientMembershipId,
        SignupId signupId,
        ReassignmentState before,
        ReassignmentState after,
        DateTimeOffset now)
    {
        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            aggregate.OrganizationId,
            recipientMembershipId,
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            signupId.Value,
            "Waitlisted signup approved",
            "Your selected waitlisted signup was approved.",
            now);
        if (notification.IsFailure)
        {
            throw new InvalidOperationException(
                "The privacy-safe reassignment notification could not be created.");
        }

        AuditEntry audit = new(
            AuditEventId.New(),
            aggregate.OrganizationId,
            actorMembershipId,
            "signup.reassigned",
            "signup",
            signupId.ToString(),
            reason ?? "Selected waitlisted signup reassigned to available capacity.",
            "waitlist_reassignment",
            idempotencyKey.ToString(),
            SerializeState(before),
            SerializeState(after),
            now);
        OutboxMessage push = new(
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
        OutboxMessage eligibility = new(
            OutboxMessageId.New(),
            aggregate.OrganizationId,
            "thread.access_changed",
            JsonSerializer.Serialize(
                new
                {
                    serviceDateId = aggregate.ServiceDateId.ToString(),
                    membershipId = recipientMembershipId.ToString(),
                    eligible = true,
                    signupId = signupId.ToString(),
                }),
            now);
        return new SignupReassignmentEffects(
            [notification.Value.Notification],
            [audit],
            [push, eligibility]);
    }

    private static string SerializeState(ReassignmentState state) =>
        JsonSerializer.Serialize(
            new
            {
                status = state.Status,
                childVersion = state.ChildVersion,
                rootSignupVersion = state.RootSignupVersion,
                waitlistOrder = state.WaitlistOrder,
            });

    private static Result<SignupSummary> InvalidInput(string message) =>
        Result.Failure<SignupSummary>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));

    private static Result<SignupSummary> Conflict(string code, string message) =>
        Result.Failure<SignupSummary>(DomainError.Conflict(code, message));

    private sealed record ReassignmentState(
        string Status,
        long ChildVersion,
        long RootSignupVersion,
        long? WaitlistOrder)
    {
        public static ReassignmentState From(
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
