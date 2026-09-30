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
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Signups.Submit;

public sealed class SubmitSignupService(
    IUnitOfWork unitOfWork,
    ISignupRepository signupRepository,
    IMembershipRepository membershipRepository,
    IIdempotencyStore idempotencyStore,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string Operation = "signup.submit";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    public ValueTask<Result<SignupSummary>> ExecuteAsync(
        SubmitSignupCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.MemberParticipantIds);
        command.HelpNeedId.EnsureValid();
        command.IdempotencyKey.EnsureValid();

        return unitOfWork.ExecuteAsync(
            token => ExecuteCoreAsync(command, token),
            cancellationToken);
    }

    private async ValueTask<Result<SignupSummary>> ExecuteCoreAsync(
        SubmitSignupCommand command,
        CancellationToken cancellationToken)
    {
        Result<string?> label = NormalizeLabel(command.Label);
        if (label.IsFailure)
        {
            return Result.Failure<SignupSummary>(label.Error);
        }

        Result participantIds = ValidateParticipantIds(command.MemberParticipantIds);
        if (participantIds.IsFailure)
        {
            return Result.Failure<SignupSummary>(participantIds.Error);
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

        RequestFingerprint fingerprint = Fingerprint(command, label.Value);
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

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingCompleted)
        {
            if (!SignupId.TryParse(receipt.Receipt.ResultReference, out SignupId completedSignupId))
            {
                throw new InvalidOperationException(
                    "The completed signup idempotency receipt contains an invalid result reference.");
            }

            return await signupRepository.GetOwnedAsync(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                completedSignupId,
                cancellationToken);
        }

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingProcessing)
        {
            return Result.Failure<SignupSummary>(
                DomainError.Conflict(
                    SignupApplicationErrorCodes.IdempotencyInProgress,
                    "The signup request is already processing."));
        }

        if (receipt.Outcome is not IdempotencyCreateOutcome.Created)
        {
            return Result.Failure<SignupSummary>(
                DomainError.Conflict(
                    SignupApplicationErrorCodes.IdempotencyMismatch,
                    "The idempotency key cannot be used for this signup request."));
        }

        Result<SignupSubmissionContext> loaded =
            await signupRepository.GetSubmissionContextAsync(
                currentActor.OrganizationId,
                command.HelpNeedId,
                currentActor.MembershipId,
                command.MemberParticipantIds,
                cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<SignupSummary>(loaded.Error);
        }

        SignupId signupId = SignupId.New();
        Result<SignupSubmitted> submitted = loaded.Value.Aggregate.Submit(
            signupId,
            loaded.Value.PrimaryMembership,
            command.Kind,
            loaded.Value.MemberParticipants,
            command.UnnamedParticipantCount,
            now);
        if (submitted.IsFailure)
        {
            return Result.Failure<SignupSummary>(submitted.Error);
        }

        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            currentActor.OrganizationId,
            loaded.Value.ManagerMembershipId,
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            signupId.Value,
            "New signup request",
            "A new signup request is pending.",
            now);
        if (notification.IsFailure)
        {
            throw new InvalidOperationException(
                "The privacy-safe signup notification could not be created.");
        }

        SignupPersistenceEffects effects = Effects(
            command,
            loaded.Value,
            submitted.Value,
            notification.Value,
            now);
        Result saved = await signupRepository.SaveSubmissionAsync(
            loaded.Value.Aggregate,
            new SignupSubmissionWrite(signupId, label.Value, effects),
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
            signupId.ToString(),
            cancellationToken);
        if (completed.Outcome != IdempotencyTransitionOutcome.Completed)
        {
            throw new InvalidOperationException(
                "The signup idempotency receipt could not be completed.");
        }

        return Result.Success(ToSummary(
            loaded.Value,
            signupId,
            command.Kind,
            label.Value,
            command.UnnamedParticipantCount,
            now));
    }

    private static SignupPersistenceEffects Effects(
        SubmitSignupCommand command,
        SignupSubmissionContext context,
        SignupSubmitted submitted,
        NotificationCreated notification,
        DateTimeOffset now)
    {
        string afterState = JsonSerializer.Serialize(
            new
            {
                status = "pending",
                totalParticipantCount = submitted.TotalParticipantCount,
                memberParticipantCount = command.MemberParticipantIds.Count,
                unnamedParticipantCount = command.UnnamedParticipantCount,
                signupVersion = context.Aggregate.Version,
            });
        AuditEntry audit = new(
            AuditEventId.New(),
            context.Aggregate.OrganizationId,
            submitted.PrimaryMembershipId,
            "signup.submitted",
            "signup",
            submitted.SignupId.ToString(),
            "Signup submitted by the primary contact.",
            "signup_submission",
            command.IdempotencyKey.ToString(),
            beforeState: null,
            afterState,
            now);
        OutboxMessage outbox = new(
            OutboxMessageId.New(),
            context.Aggregate.OrganizationId,
            "notification.push_requested",
            JsonSerializer.Serialize(
                new
                {
                    notificationId = notification.Notification.Id.ToString(),
                    recipientMembershipId = notification.Notification.RecipientMembershipId.ToString(),
                    resourceType = "signup",
                    resourceId = submitted.SignupId.ToString(),
                }),
            now);
        return new SignupPersistenceEffects(
            [notification.Notification],
            [audit],
            [outbox]);
    }

    private static SignupSummary ToSummary(
        SignupSubmissionContext context,
        SignupId signupId,
        Domain.Common.Enums.SignupKind kind,
        string? label,
        int unnamedParticipantCount,
        DateTimeOffset submittedAt)
    {
        SignupParticipantSummary[] participants = context.MemberParticipants
            .OrderBy(participant => participant.DisplayName, StringComparer.Ordinal)
            .ThenBy(participant => participant.Id.Value)
            .Select(participant => new SignupParticipantSummary(
                participant.Id,
                participant.DisplayName))
            .ToArray();
        return new SignupSummary(
            signupId,
            context.Aggregate.ServiceDateId,
            context.Aggregate.HelpNeedId,
            context.Category,
            new SignupParticipantSummary(
                context.PrimaryMembership.Id,
                context.PrimaryMembership.DisplayName),
            kind,
            label,
            participants,
            unnamedParticipantCount,
            1 + participants.Length + unnamedParticipantCount,
            Domain.Common.Enums.SignupStatus.Pending,
            submittedAt,
            LastTransitionAt: null,
            WaitlistOrder: null,
            Version: 0,
            context.Aggregate.Version);
    }

    private static Result<string?> NormalizeLabel(string? value)
    {
        SignupLabelValidationResult validation = SignupLabelPolicy.Normalize(value);
        if (validation.IsSuccess)
        {
            return Result.Success(validation.Value);
        }

        if (validation.Error == SignupLabelValidationError.TooLarge)
        {
            return Result.Failure<string?>(
                DomainError.PayloadTooLarge(
                    "The signup label exceeds the permitted content size."));
        }

        return Result.Failure<string?>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                "The signup label must use the documented non-identifying format."));
    }

    private static Result ValidateParticipantIds(
        IReadOnlyCollection<MembershipId> participantIds)
    {
        if (participantIds.Count > ApplicationLimits.MaximumNamedParticipants
            || participantIds.Any(id => !id.IsValid)
            || participantIds.Distinct().Count() != participantIds.Count)
        {
            return Result.Failure(
                DomainError.Validation(
                    SignupApplicationErrorCodes.InvalidSignupRequest,
                    "Referenced participant IDs must be valid, distinct, and within the allowed count."));
        }

        return Result.Success();
    }

    private static RequestFingerprint Fingerprint(
        SubmitSignupCommand command,
        string? normalizedLabel)
    {
        string canonical = JsonSerializer.Serialize(
            new
            {
                helpNeedId = command.HelpNeedId.ToString(),
                kind = command.Kind.ToString(),
                label = normalizedLabel,
                memberParticipantIds = command.MemberParticipantIds
                    .Select(id => id.ToString())
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                unnamedParticipantCount = command.UnnamedParticipantCount,
            });
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return RequestFingerprint.FromSha256(Convert.ToHexString(hash));
    }
}
