using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Application.Dates.Management;

public sealed class DateManagementService(
    IUnitOfWork unitOfWork,
    IServiceDateRepository serviceDateRepository,
    ISignupRepository signupRepository,
    IThreadRepository threadRepository,
    INotificationWriter notificationWriter,
    IAuditWriter auditWriter,
    IOutboxWriter outboxWriter,
    IMembershipRepository membershipRepository,
    IIdempotencyStore idempotencyStore,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string CloseOperation = "service_date.close";
    private const string CancelOperation = "service_date.cancel";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    public ValueTask<Result<ServiceDateSummary>> EditDateAsync(
        EditServiceDateCommand command,
        long expectedDateVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.ServiceDateId.EnsureValid();
        return unitOfWork.ExecuteAsync(
            token => EditDateCoreAsync(command, expectedDateVersion, token),
            cancellationToken);
    }

    public ValueTask<Result<HelpNeedSummary>> EditNeedAsync(
        EditHelpNeedCommand command,
        long expectedNeedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.HelpNeedId.EnsureValid();
        return unitOfWork.ExecuteAsync(
            token => EditNeedCoreAsync(command, expectedNeedVersion, token),
            cancellationToken);
    }

    public ValueTask<Result<ServiceDateSummary>> CloseAsync(
        CloseServiceDateCommand command,
        long expectedDateVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.ServiceDateId.EnsureValid();
        command.IdempotencyKey.EnsureValid();
        return unitOfWork.ExecuteAsync(
            token => CloseCoreAsync(command, expectedDateVersion, token),
            cancellationToken);
    }

    public ValueTask<Result<ServiceDateSummary>> CancelAsync(
        CancelServiceDateCommand command,
        long expectedDateVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.ServiceDateId.EnsureValid();
        command.IdempotencyKey.EnsureValid();
        return unitOfWork.ExecuteAsync(
            token => CancelCoreAsync(command, expectedDateVersion, token),
            cancellationToken);
    }

    private async ValueTask<Result<ServiceDateSummary>> EditDateCoreAsync(
        EditServiceDateCommand command,
        long expectedDateVersion,
        CancellationToken cancellationToken)
    {
        if (expectedDateVersion < 0)
        {
            return Invalid<ServiceDateSummary>("The expected service-date version must be nonnegative.");
        }

        Result<ManagedDateContext> managed = await LoadManagedDateAsync(
            command.ServiceDateId,
            cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(managed.Error);
        }

        if (managed.Value.LoadedDate.ServiceDate.Version != expectedDateVersion)
        {
            return Result.Failure<ServiceDateSummary>(DateApplicationErrorCodes.StaleVersion());
        }

        Result authority = await RevalidateManagedAuthorityAsync(
            command.ServiceDateId,
            cancellationToken);
        if (authority.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(authority.Error);
        }

        ServiceDate date = managed.Value.LoadedDate.ServiceDate;
        string before = SerializeDate(date);
        Result<ServiceDateChanged> changed = date.Edit(
            command.Title,
            command.Instructions,
            command.StartsAt,
            command.EndsAt,
            command.CancellationDeadlineAt);
        if (changed.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(changed.Error);
        }

        DateTimeOffset now = clock.UtcNow;
        Result saved = await serviceDateRepository.SaveAsync(
            managed.Value.LoadedDate,
            CreateDateEffects(
                "service_date.updated",
                date,
                managed.Value.Actor.MembershipId,
                now,
                "Service date edited by the managing Food Incharge.",
                "date_management",
                before,
                correlationId: date.Id.ToString()),
            cancellationToken);
        return saved.IsSuccess
            ? Result.Success(ToDateSummary(date, managed.Value.LoadedDate.Availability))
            : Result.Failure<ServiceDateSummary>(saved.Error);
    }

    private async ValueTask<Result<HelpNeedSummary>> EditNeedCoreAsync(
        EditHelpNeedCommand command,
        long expectedNeedVersion,
        CancellationToken cancellationToken)
    {
        if (expectedNeedVersion < 0)
        {
            return Invalid<HelpNeedSummary>("The expected help-need version must be nonnegative.");
        }

        Result<ManagedDateContext> managed = await LoadManagedNeedAsync(
            command.HelpNeedId,
            cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<HelpNeedSummary>(managed.Error);
        }

        HelpNeed currentNeed = managed.Value.LoadedDate.ServiceDate.HelpNeeds.Single(
            need => need.Id == command.HelpNeedId);
        if (currentNeed.Version != expectedNeedVersion)
        {
            return Result.Failure<HelpNeedSummary>(DateApplicationErrorCodes.StaleVersion());
        }

        Result authority = await RevalidateManagedAuthorityAsync(
            managed.Value.LoadedDate.ServiceDate.Id,
            cancellationToken);
        if (authority.IsFailure)
        {
            return Result.Failure<HelpNeedSummary>(authority.Error);
        }

        Result<HelpNeedSignups> aggregateResult = await signupRepository.GetAsync(
            currentActor.OrganizationId,
            command.HelpNeedId,
            cancellationToken);
        if (aggregateResult.IsFailure)
        {
            return Result.Failure<HelpNeedSummary>(aggregateResult.Error);
        }

        HelpNeedSignups aggregate = aggregateResult.Value;
        long approvedParticipants = aggregate.Signups
            .Where(signup => signup.Status == SignupStatus.Approved)
            .Sum(signup => (long)signup.TotalParticipantCount);
        if (command.Capacity.HasValue && approvedParticipants > command.Capacity.Value)
        {
            return Result.Failure<HelpNeedSummary>(
                DomainError.Conflict(
                    ErrorCodes.CapacityUnavailable,
                    "The proposed capacity is below the approved participant count."));
        }

        ServiceDate date = managed.Value.LoadedDate.ServiceDate;
        string before = SerializeNeed(currentNeed);
        Result<HelpNeedChanged> changed = date.ChangeNeed(
            command.HelpNeedId,
            command.Instructions,
            command.Capacity,
            command.Status,
            clock.UtcNow);
        if (changed.IsFailure)
        {
            return Result.Failure<HelpNeedSummary>(changed.Error);
        }

        HelpNeed updatedNeed = date.HelpNeeds.Single(need => need.Id == command.HelpNeedId);
        int? availability = updatedNeed.Capacity is null
            ? null
            : Math.Max(0, updatedNeed.Capacity.Value - checked((int)approvedParticipants));
        Result saved = await serviceDateRepository.SaveNeedAsync(
            managed.Value.LoadedDate,
            command.HelpNeedId,
            expectedNeedVersion,
            command.Capacity != currentNeed.Capacity ? aggregate.OriginalVersion : null,
            new DatePersistenceEffects(
                [
                    new AuditEntry(
                        AuditEventId.New(),
                        date.OrganizationId,
                        managed.Value.Actor.MembershipId,
                        "help_need.updated",
                        "help_need",
                        command.HelpNeedId.ToString(),
                        "Help need edited by the managing Food Incharge.",
                        "date_management",
                        command.HelpNeedId.ToString(),
                        before,
                        SerializeNeed(updatedNeed),
                        changed.Value.OccurredAt),
                ],
                []),
            cancellationToken);
        return saved.IsSuccess
            ? Result.Success(ToNeedSummary(updatedNeed, availability))
            : Result.Failure<HelpNeedSummary>(saved.Error);
    }

    private async ValueTask<Result<ServiceDateSummary>> CloseCoreAsync(
        CloseServiceDateCommand command,
        long expectedDateVersion,
        CancellationToken cancellationToken)
    {
        Result<string?> normalizedReason = NormalizeReason(command.Reason);
        if (normalizedReason.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(normalizedReason.Error);
        }

        if (expectedDateVersion < 0)
        {
            return Invalid<ServiceDateSummary>("The expected service-date version must be nonnegative.");
        }

        Result<ManagedDateContext> managed = await LoadManagedDateAsync(
            command.ServiceDateId,
            cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(managed.Error);
        }

        RequestFingerprint fingerprint = Fingerprint(
            command.ServiceDateId,
            normalizedReason.Value,
            expectedDateVersion);
        DateTimeOffset now = clock.UtcNow;
        IdempotencyCreateResult receipt = await idempotencyStore.TryCreateProcessingAsync(
            new IdempotencyCreateRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.IdempotencyKey,
                CloseOperation,
                fingerprint,
                now,
                now.Add(IdempotencyLifetime)),
            cancellationToken);
        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingProcessing)
        {
            return Conflict<ServiceDateSummary>(
                DateApplicationErrorCodes.IdempotencyInProgress,
                "The date-close request is already processing.");
        }

        if (receipt.Outcome is IdempotencyCreateOutcome.ExistingFailed
            or IdempotencyCreateOutcome.RequestMismatch
            or IdempotencyCreateOutcome.Expired)
        {
            return Conflict<ServiceDateSummary>(
                DateApplicationErrorCodes.IdempotencyMismatch,
                "The idempotency key cannot be used for this date-close request.");
        }

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingCompleted)
        {
            if (!ServiceDateId.TryParse(receipt.Receipt.ResultReference, out ServiceDateId completedDateId)
                || completedDateId != command.ServiceDateId)
            {
                throw new InvalidOperationException(
                    "The completed date-close receipt contains an invalid result reference.");
            }

            return Result.Success(
                ToDateSummary(
                    managed.Value.LoadedDate.ServiceDate,
                    managed.Value.LoadedDate.Availability));
        }

        if (managed.Value.LoadedDate.ServiceDate.Version != expectedDateVersion)
        {
            return Result.Failure<ServiceDateSummary>(DateApplicationErrorCodes.StaleVersion());
        }

        Result authority = await RevalidateManagedAuthorityAsync(
            command.ServiceDateId,
            cancellationToken);
        if (authority.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(authority.Error);
        }

        if (receipt.Outcome != IdempotencyCreateOutcome.Created)
        {
            throw new InvalidOperationException(
                $"Unsupported date-close idempotency outcome: {receipt.Outcome}.");
        }

        Result<LoadedDateThread?> thread = await LoadThreadIfPresentAsync(
            managed.Value.LoadedDate.ServiceDate.Id,
            cancellationToken);
        if (thread.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(thread.Error);
        }

        ServiceDate date = managed.Value.LoadedDate.ServiceDate;
        string before = SerializeDate(date);
        Result<ServiceDateClosed> closed = date.Close(now);
        if (closed.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(closed.Error);
        }

        Result threadLock = await LockExistingThreadAsync(
            thread.Value,
            managed.Value.Actor,
            date,
            normalizedReason.Value ?? "Service date closed by the managing Food Incharge.",
            "date_close",
            command.IdempotencyKey.ToString(),
            now,
            cancellationToken);
        if (threadLock.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(threadLock.Error);
        }

        Result saved = await serviceDateRepository.SaveAsync(
            managed.Value.LoadedDate,
            CreateDateEffects(
                "service_date.closed",
                date,
                managed.Value.Actor.MembershipId,
                now,
                normalizedReason.Value ?? "Service date closed by the managing Food Incharge.",
                "date_management",
                before,
                correlationId: command.IdempotencyKey.ToString()),
            cancellationToken);
        if (saved.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(saved.Error);
        }

        IdempotencyTransitionResult completed = await idempotencyStore.TryCompleteAsync(
            new IdempotencyRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.IdempotencyKey,
                CloseOperation,
                fingerprint),
            command.ServiceDateId.ToString(),
            cancellationToken);
        if (completed.Outcome != IdempotencyTransitionOutcome.Completed)
        {
            throw new InvalidOperationException(
                "The date-close idempotency receipt could not be completed.");
        }

        return Result.Success(ToDateSummary(date, managed.Value.LoadedDate.Availability));
    }

    private async ValueTask<Result<ServiceDateSummary>> CancelCoreAsync(
        CancelServiceDateCommand command,
        long expectedDateVersion,
        CancellationToken cancellationToken)
    {
        Result<string?> normalizedReason = NormalizeReason(command.Reason);
        if (normalizedReason.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(normalizedReason.Error);
        }

        if (expectedDateVersion < 0)
        {
            return Invalid<ServiceDateSummary>("The expected service-date version must be nonnegative.");
        }

        Result<ManagedDateContext> managed = await LoadManagedDateAsync(
            command.ServiceDateId,
            cancellationToken);
        if (managed.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(managed.Error);
        }

        RequestFingerprint fingerprint = Fingerprint(
            command.ServiceDateId,
            normalizedReason.Value,
            expectedDateVersion);
        DateTimeOffset now = clock.UtcNow;
        IdempotencyCreateResult receipt = await idempotencyStore.TryCreateProcessingAsync(
            new IdempotencyCreateRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.IdempotencyKey,
                CancelOperation,
                fingerprint,
                now,
                now.Add(IdempotencyLifetime)),
            cancellationToken);
        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingProcessing)
        {
            return Conflict<ServiceDateSummary>(
                DateApplicationErrorCodes.IdempotencyInProgress,
                "The date-cancel request is already processing.");
        }

        if (receipt.Outcome is IdempotencyCreateOutcome.ExistingFailed
            or IdempotencyCreateOutcome.RequestMismatch
            or IdempotencyCreateOutcome.Expired)
        {
            return Conflict<ServiceDateSummary>(
                DateApplicationErrorCodes.IdempotencyMismatch,
                "The idempotency key cannot be used for this date-cancel request.");
        }

        if (receipt.Outcome == IdempotencyCreateOutcome.ExistingCompleted)
        {
            if (!ServiceDateId.TryParse(receipt.Receipt.ResultReference, out ServiceDateId completedDateId)
                || completedDateId != command.ServiceDateId)
            {
                throw new InvalidOperationException(
                    "The completed date-cancel receipt contains an invalid result reference.");
            }

            return Result.Success(
                ToDateSummary(
                    managed.Value.LoadedDate.ServiceDate,
                    managed.Value.LoadedDate.Availability));
        }

        if (managed.Value.LoadedDate.ServiceDate.Version != expectedDateVersion)
        {
            return Result.Failure<ServiceDateSummary>(DateApplicationErrorCodes.StaleVersion());
        }

        Result authority = await RevalidateManagedAuthorityAsync(
            command.ServiceDateId,
            cancellationToken);
        if (authority.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(authority.Error);
        }

        if (receipt.Outcome != IdempotencyCreateOutcome.Created)
        {
            throw new InvalidOperationException(
                $"Unsupported date-cancel idempotency outcome: {receipt.Outcome}.");
        }

        Result<LoadedDateThread?> thread = await LoadThreadIfPresentAsync(
            managed.Value.LoadedDate.ServiceDate.Id,
            cancellationToken);
        if (thread.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(thread.Error);
        }

        Result<CancellationPlan> plan = await CreateCancellationPlanAsync(
            managed.Value.LoadedDate.ServiceDate,
            normalizedReason.Value ?? "Service date cancelled by the managing Food Incharge.",
            command.IdempotencyKey.ToString(),
            now,
            cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(plan.Error);
        }

        Result threadLock = await LockExistingThreadAsync(
            thread.Value,
            managed.Value.Actor,
            managed.Value.LoadedDate.ServiceDate,
            normalizedReason.Value ?? "Service date cancelled by the managing Food Incharge.",
            "date_cancellation",
            command.IdempotencyKey.ToString(),
            now,
            cancellationToken);
        if (threadLock.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(threadLock.Error);
        }

        ServiceDate date = managed.Value.LoadedDate.ServiceDate;
        string before = SerializeDate(date);
        Result<ServiceDateCancelled> cancelled = date.Cancel(now);
        if (cancelled.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(cancelled.Error);
        }

        foreach (HelpNeedSignups aggregate in plan.Value.Aggregates)
        {
            Result savedAggregate = await signupRepository.SaveDateCancellationAsync(
                aggregate,
                managed.Value.Actor.MembershipId,
                cancellationToken);
            if (savedAggregate.IsFailure)
            {
                return Result.Failure<ServiceDateSummary>(savedAggregate.Error);
            }
        }

        Result dateSaved = await serviceDateRepository.SaveAsync(
            managed.Value.LoadedDate,
            CreateDateEffects(
                "service_date.cancelled",
                date,
                managed.Value.Actor.MembershipId,
                now,
                normalizedReason.Value ?? "Service date cancelled by the managing Food Incharge.",
                "date_cancellation",
                before,
                correlationId: command.IdempotencyKey.ToString()),
            cancellationToken);
        if (dateSaved.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(dateSaved.Error);
        }

        foreach (Notification notification in plan.Value.Notifications)
        {
            await notificationWriter.AddAsync(notification, cancellationToken);
        }

        foreach (AuditEntry audit in plan.Value.AuditEntries)
        {
            await auditWriter.WriteAsync(audit, cancellationToken);
        }

        foreach (OutboxMessage outbox in plan.Value.OutboxMessages)
        {
            await outboxWriter.AddAsync(outbox, cancellationToken);
        }

        IdempotencyTransitionResult completed = await idempotencyStore.TryCompleteAsync(
            new IdempotencyRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.IdempotencyKey,
                CancelOperation,
                fingerprint),
            command.ServiceDateId.ToString(),
            cancellationToken);
        if (completed.Outcome != IdempotencyTransitionOutcome.Completed)
        {
            throw new InvalidOperationException(
                "The date-cancel idempotency receipt could not be completed.");
        }

        return Result.Success(
            ToDateSummary(
                date,
                date.HelpNeeds.ToDictionary(need => need.Id, need => need.Capacity)));
    }

    private async ValueTask<Result<CancellationPlan>> CreateCancellationPlanAsync(
        ServiceDate date,
        string reason,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<HelpNeedSignups> aggregates = [];
        List<AuditEntry> audits = [];
        Dictionary<MembershipId, bool> affectedContacts = [];

        foreach (HelpNeed need in date.HelpNeeds.OrderBy(need => need.Id.ToString(), StringComparer.Ordinal))
        {
            Result<HelpNeedSignups> aggregateResult = await signupRepository.GetAsync(
                date.OrganizationId,
                need.Id,
                cancellationToken);
            if (aggregateResult.IsFailure)
            {
                return Result.Failure<CancellationPlan>(aggregateResult.Error);
            }

            HelpNeedSignups aggregate = aggregateResult.Value;
            foreach (Signup signup in aggregate.Signups
                         .Where(signup => signup.Status is SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted)
                         .OrderBy(signup => signup.Id.ToString(), StringComparer.Ordinal)
                         .ToArray())
            {
                CancellationAuditState before = CancellationAuditState.From(
                    signup.Status,
                    signup.Version,
                    aggregate.Version,
                    signup.WaitlistOrder);
                bool approved = signup.Status == SignupStatus.Approved;
                affectedContacts[signup.PrimaryMembershipId] =
                    affectedContacts.TryGetValue(signup.PrimaryMembershipId, out bool priorApproved)
                        ? priorApproved || approved
                        : approved;

                Result<SignupTransitioned> cancelled = aggregate.Cancel(signup.Id, now);
                if (cancelled.IsFailure)
                {
                    return Result.Failure<CancellationPlan>(cancelled.Error);
                }

                Signup changed = aggregate.Signups.Single(candidate => candidate.Id == signup.Id);
                audits.Add(
                    new AuditEntry(
                        AuditEventId.New(),
                        aggregate.OrganizationId,
                        currentActor.MembershipId,
                        "signup.cancelled",
                        "signup",
                        signup.Id.ToString(),
                        reason,
                        "date_cancellation",
                        correlationId,
                        SerializeCancellationAuditState(before),
                        SerializeCancellationAuditState(
                            CancellationAuditState.From(
                                changed.Status,
                                changed.Version,
                                aggregate.Version,
                                changed.WaitlistOrder)),
                        now));
            }

            aggregates.Add(aggregate);
        }

        List<Notification> notifications = [];
        List<OutboxMessage> outbox = [];
        foreach ((MembershipId membershipId, bool hadApprovedAccess) in affectedContacts
                     .OrderBy(item => item.Key.ToString(), StringComparer.Ordinal))
        {
            Result<NotificationCreated> created = Notification.Create(
                NotificationId.New(),
                date.OrganizationId,
                membershipId,
                NotificationType.ServiceDateCancelled,
                NotificationResourceType.ServiceDate,
                date.Id.Value,
                "Service date cancelled",
                "The service date was cancelled and your active signup was updated.",
                now);
            if (created.IsFailure)
            {
                throw new InvalidOperationException(
                    "The privacy-safe date-cancellation notification could not be created.");
            }

            notifications.Add(created.Value.Notification);
            outbox.Add(
                new OutboxMessage(
                    OutboxMessageId.New(),
                    date.OrganizationId,
                    "notification.push_requested",
                    JsonSerializer.Serialize(
                        new
                        {
                            notificationId = created.Value.Notification.Id.ToString(),
                            recipientMembershipId = membershipId.ToString(),
                            resourceType = "serviceDate",
                            resourceId = date.Id.ToString(),
                        }),
                    now));

            if (hadApprovedAccess)
            {
                outbox.Add(
                    new OutboxMessage(
                        OutboxMessageId.New(),
                        date.OrganizationId,
                        "thread.access_changed",
                        JsonSerializer.Serialize(
                            new
                            {
                                serviceDateId = date.Id.ToString(),
                                membershipId = membershipId.ToString(),
                                eligible = false,
                            }),
                        now));
            }
        }

        return Result.Success(new CancellationPlan(aggregates, notifications, audits, outbox));
    }

    private async ValueTask<Result<ManagedDateContext>> LoadManagedDateAsync(
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<ManagedDateContext>(actor.Error);
        }

        Result<LoadedServiceDate> loaded = await serviceDateRepository.GetAsync(
            currentActor.OrganizationId,
            serviceDateId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ManagedDateContext>(loaded.Error);
        }

        return IsManagingFoodIncharge(actor.Value, loaded.Value.ServiceDate)
            ? Result.Success(new ManagedDateContext(actor.Value, loaded.Value))
            : Result.Failure<ManagedDateContext>(DateApplicationErrorCodes.Forbidden());
    }

    private async ValueTask<Result<ManagedDateContext>> LoadManagedNeedAsync(
        HelpNeedId helpNeedId,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<ManagedDateContext>(actor.Error);
        }

        Result<LoadedServiceDate> loaded = await serviceDateRepository.GetByHelpNeedAsync(
            currentActor.OrganizationId,
            helpNeedId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ManagedDateContext>(loaded.Error);
        }

        return IsManagingFoodIncharge(actor.Value, loaded.Value.ServiceDate)
            ? Result.Success(new ManagedDateContext(actor.Value, loaded.Value))
            : Result.Failure<ManagedDateContext>(DateApplicationErrorCodes.Forbidden());
    }

    private async ValueTask<Result<LoadedDateThread?>> LoadThreadIfPresentAsync(
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken)
    {
        Result<LoadedDateThread> thread = await threadRepository.GetByServiceDateAsync(
            currentActor.OrganizationId,
            serviceDateId,
            cancellationToken);
        return thread.IsFailure
            ? thread.Error.Type == ErrorType.NotFound
                ? Result.Success<LoadedDateThread?>(null)
                : Result.Failure<LoadedDateThread?>(thread.Error)
            : Result.Success<LoadedDateThread?>(thread.Value);
    }

    private async ValueTask<Result> LockExistingThreadAsync(
        LoadedDateThread? loadedThread,
        ActiveMembershipContext actor,
        ServiceDate date,
        string reason,
        string purpose,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (loadedThread is null || loadedThread.Thread.Status == ThreadStatus.Locked)
        {
            return Result.Success();
        }

        Result<Membership> actorMembership = Membership.Rehydrate(
            actor.MembershipId,
            actor.OrganizationId,
            actor.UserId,
            actor.DisplayName,
            MembershipStatus.Active,
            actor.EligibleAsNamedParticipant,
            actor.Roles);
        if (actorMembership.IsFailure)
        {
            return Result.Failure(actorMembership.Error);
        }

        string before = SerializeThread(loadedThread.Thread);
        Result<ThreadLocked> locked = loadedThread.Thread.Lock(
            reason,
            actorMembership.Value,
            date,
            now);
        if (locked.IsFailure)
        {
            return Result.Failure(locked.Error);
        }

        return await threadRepository.SaveAsync(
            loadedThread,
            new ThreadPersistenceEffects(
                [],
                [
                    new AuditEntry(
                        AuditEventId.New(),
                        loadedThread.Thread.OrganizationId,
                        actor.MembershipId,
                        "thread.locked",
                        "thread",
                        loadedThread.Thread.Id.ToString(),
                        reason,
                        purpose,
                        correlationId,
                        before,
                        SerializeThread(loadedThread.Thread),
                        now),
                ],
                [],
                [],
                [
                    new ThreadModerationWrite(
                        Guid.CreateVersion7(),
                        loadedThread.Thread.OrganizationId,
                        loadedThread.Thread.Id,
                        null,
                        actor.MembershipId,
                        "lock",
                        reason,
                        now),
                ]),
            cancellationToken);
    }

    private ValueTask<Result<ActiveMembershipContext>> ResolveActorAsync(CancellationToken cancellationToken) =>
        membershipRepository.ResolveActiveActorAsync(
            currentActor.UserId,
            currentActor.MembershipId,
            currentActor.OrganizationId,
            cancellationToken);

    private async ValueTask<Result> RevalidateManagedAuthorityAsync(
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure(actor.Error);
        }

        if (!actor.Value.Roles.Contains(OrganizationRole.FoodIncharge))
        {
            return Result.Failure(DateApplicationErrorCodes.Forbidden());
        }

        return await serviceDateRepository.RevalidateManagedAuthorityAsync(
            currentActor.OrganizationId,
            serviceDateId,
            currentActor.MembershipId,
            cancellationToken);
    }

    private static bool IsManagingFoodIncharge(
        ActiveMembershipContext actor,
        ServiceDate date) =>
        actor.Roles.Contains(OrganizationRole.FoodIncharge)
        && actor.MembershipId == date.ManagerMembershipId;

    private static Result<string?> NormalizeReason(string? reason)
    {
        string? normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalized is not null
            && (normalized.EnumerateRunes().Count() > ApplicationLimits.MaximumReasonUnicodeScalars
                || Encoding.UTF8.GetByteCount(normalized) > ApplicationLimits.MaximumReasonUtf8Bytes))
        {
            return Result.Failure<string?>(
                DomainError.PayloadTooLarge(
                    "The administrative reason exceeds the permitted content size."));
        }

        return Result.Success(normalized);
    }

    private static RequestFingerprint Fingerprint(
        ServiceDateId serviceDateId,
        string? reason,
        long expectedDateVersion)
    {
        string canonical = JsonSerializer.Serialize(
            new
            {
                serviceDateId = serviceDateId.ToString(),
                reason,
                expectedDateVersion,
            });
        return RequestFingerprint.FromSha256(
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static DatePersistenceEffects CreateDateEffects(
        string action,
        ServiceDate date,
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt,
        string reason,
        string purpose,
        string beforeState,
        string correlationId) =>
        new(
            [
                new AuditEntry(
                    AuditEventId.New(),
                    date.OrganizationId,
                    actorMembershipId,
                    action,
                    "service_date",
                    date.Id.ToString(),
                    reason,
                    purpose,
                    correlationId,
                    beforeState,
                    SerializeDate(date),
                    occurredAt),
            ],
            []);

    private static ServiceDateSummary ToDateSummary(
        ServiceDate date,
        IReadOnlyDictionary<HelpNeedId, int?> availability) =>
        new(
            date.Id,
            date.Title,
            date.Instructions,
            date.StartsAt,
            date.EndsAt,
            date.CancellationDeadlineAt,
            date.ManagerMembershipId,
            date.Status,
            date.Version,
            date.HelpNeeds
                .OrderBy(need => need.Category)
                .Select(need => ToNeedSummary(need, availability.GetValueOrDefault(need.Id, need.Capacity)))
                .ToArray());

    private static HelpNeedSummary ToNeedSummary(HelpNeed need, int? availability) =>
        new(
            need.Id,
            need.Category,
            need.Instructions,
            availability,
            need.Status,
            need.Version);

    private static string SerializeDate(ServiceDate date) =>
        JsonSerializer.Serialize(
            new
            {
                id = date.Id.ToString(),
                title = date.Title,
                instructions = date.Instructions,
                startsAt = date.StartsAt,
                endsAt = date.EndsAt,
                cancellationDeadlineAt = date.CancellationDeadlineAt,
                status = date.Status.ToString().ToLowerInvariant(),
                version = date.Version,
                helpNeeds = date.HelpNeeds.Select(
                    need => new
                    {
                        id = need.Id.ToString(),
                        category = need.Category.ToString().ToLowerInvariant(),
                        instructions = need.Instructions,
                        capacity = need.Capacity,
                        status = need.Status.ToString().ToLowerInvariant(),
                        version = need.Version,
                    }),
            });

    private static string SerializeNeed(HelpNeed need) =>
        JsonSerializer.Serialize(
            new
            {
                id = need.Id.ToString(),
                category = need.Category.ToString().ToLowerInvariant(),
                instructions = need.Instructions,
                capacity = need.Capacity,
                status = need.Status.ToString().ToLowerInvariant(),
                version = need.Version,
            });

    private static string SerializeThread(DateThread thread) =>
        JsonSerializer.Serialize(
            new
            {
                id = thread.Id.ToString(),
                serviceDateId = thread.ServiceDateId.ToString(),
                status = thread.Status.ToString().ToLowerInvariant(),
                lockedAt = thread.LockedAt,
                version = thread.Version,
            });

    private static string SerializeCancellationAuditState(CancellationAuditState state) =>
        JsonSerializer.Serialize(
            new
            {
                status = state.Status,
                childVersion = state.ChildVersion,
                rootSignupVersion = state.RootSignupVersion,
                waitlistOrder = state.WaitlistOrder,
            });

    private static Result<T> Invalid<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                DateApplicationErrorCodes.InvalidDateRequest,
                message));

    private static Result<T> Conflict<T>(string code, string message) =>
        Result.Failure<T>(DomainError.Conflict(code, message));

    private sealed record ManagedDateContext(
        ActiveMembershipContext Actor,
        LoadedServiceDate LoadedDate);

    private sealed record CancellationPlan(
        IReadOnlyCollection<HelpNeedSignups> Aggregates,
        IReadOnlyCollection<Notification> Notifications,
        IReadOnlyCollection<AuditEntry> AuditEntries,
        IReadOnlyCollection<OutboxMessage> OutboxMessages);

    private sealed record CancellationAuditState(
        string Status,
        long ChildVersion,
        long RootSignupVersion,
        long? WaitlistOrder)
    {
        public static CancellationAuditState From(
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
