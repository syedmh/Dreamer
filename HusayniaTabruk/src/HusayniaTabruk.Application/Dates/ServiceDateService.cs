using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;

namespace HusayniaTabruk.Application.Dates;

public sealed class ServiceDateService(
    IUnitOfWork unitOfWork,
    IServiceDateRepository serviceDateRepository,
    IMembershipRepository membershipRepository,
    IIdempotencyStore idempotencyStore,
    ICurrentActor currentActor,
    IClock clock)
{
    private const string OpenOperation = "service_date.open";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    public ValueTask<Result<ServiceDateSummary>> CreateAsync(
        CreateServiceDateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return unitOfWork.ExecuteAsync(
            token => CreateCoreAsync(command, token),
            cancellationToken);
    }

    public ValueTask<Result<HelpNeedSummary>> AddNeedAsync(
        AddHelpNeedCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return unitOfWork.ExecuteAsync(
            token => AddNeedCoreAsync(command, token),
            cancellationToken);
    }

    public ValueTask<Result<ServiceDateSummary>> OpenAsync(
        OpenServiceDateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.IdempotencyKey.EnsureValid();
        return unitOfWork.ExecuteAsync(
            token => OpenCoreAsync(command, token),
            cancellationToken);
    }

    public async ValueTask<Result<OpenServiceDatesPage>> ListOpenAsync(
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        return actor.IsFailure
            ? Result.Failure<OpenServiceDatesPage>(actor.Error)
            : await serviceDateRepository.ListOpenAsync(
                currentActor.OrganizationId,
                cursor,
                pageSize,
                cancellationToken);
    }

    public async ValueTask<Result<ServiceDateSummary>> GetAsync(
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken = default)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(actor.Error);
        }

        Result<LoadedServiceDate> loaded = await serviceDateRepository.GetAsync(
            currentActor.OrganizationId,
            serviceDateId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(loaded.Error);
        }

        ServiceDate date = loaded.Value.ServiceDate;
        bool managesDate = date.ManagerMembershipId == currentActor.MembershipId
            && actor.Value.Roles.Contains(OrganizationRole.FoodIncharge);
        if (date.Status != ServiceDateStatus.Open && !managesDate)
        {
            return Result.Failure<ServiceDateSummary>(DateApplicationErrorCodes.NotFound());
        }

        return Result.Success(
            ToSummary(
                date,
                loaded.Value.Availability,
                includeClosedNeeds: managesDate));
    }

    private async ValueTask<Result<ServiceDateSummary>> CreateCoreAsync(
        CreateServiceDateCommand command,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await RequireManagingFoodInchargeAsync(
            command.ManagerMembershipId,
            cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(actor.Error);
        }

        Result<ServiceDate> created = ServiceDate.Create(
            ServiceDateId.New(),
            currentActor.OrganizationId,
            command.Title,
            command.Instructions,
            command.StartsAt,
            command.EndsAt,
            command.CancellationDeadlineAt,
            command.ManagerMembershipId);
        if (created.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(created.Error);
        }

        DateTimeOffset now = clock.UtcNow;
        Result saved = await serviceDateRepository.CreateAsync(
            created.Value,
            Effects(
                "service_date.created",
                created.Value,
                currentActor.MembershipId,
                now,
                "Service date created by its managing Food Incharge.",
                beforeState: null),
            cancellationToken);
        return saved.IsSuccess
            ? Result.Success(ToSummary(created.Value, new Dictionary<HelpNeedId, int?>(), includeClosedNeeds: true))
            : Result.Failure<ServiceDateSummary>(saved.Error);
    }

    private async ValueTask<Result<HelpNeedSummary>> AddNeedCoreAsync(
        AddHelpNeedCommand command,
        CancellationToken cancellationToken)
    {
        Result<LoadedServiceDate> loaded = await LoadManagedAsync(
            command.ServiceDateId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<HelpNeedSummary>(loaded.Error);
        }

        string before = Serialize(loaded.Value.ServiceDate);
        Result<HelpNeedAdded> added = loaded.Value.ServiceDate.AddNeed(
            HelpNeedId.New(),
            command.Category,
            command.Instructions,
            command.Capacity,
            clock.UtcNow);
        if (added.IsFailure)
        {
            return Result.Failure<HelpNeedSummary>(added.Error);
        }

        Result saved = await serviceDateRepository.SaveAsync(
            loaded.Value,
            Effects(
                "help_need.created",
                loaded.Value.ServiceDate,
                currentActor.MembershipId,
                added.Value.OccurredAt,
                "Help need created by the managing Food Incharge.",
                before),
            cancellationToken);
        return saved.IsSuccess
            ? Result.Success(ToSummary(added.Value.HelpNeed))
            : Result.Failure<HelpNeedSummary>(saved.Error);
    }

    private async ValueTask<Result<ServiceDateSummary>> OpenCoreAsync(
        OpenServiceDateCommand command,
        CancellationToken cancellationToken)
    {
        Result<LoadedServiceDate> loaded = await LoadManagedAsync(
            command.ServiceDateId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(loaded.Error);
        }

        RequestFingerprint fingerprint = Fingerprint(command.ServiceDateId.ToString());
        DateTimeOffset now = clock.UtcNow;
        IdempotencyCreateResult idempotency = await idempotencyStore.TryCreateProcessingAsync(
            new IdempotencyCreateRequest(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                command.IdempotencyKey,
                OpenOperation,
                fingerprint,
                now,
                now.Add(IdempotencyLifetime)),
            cancellationToken);

        if (idempotency.Outcome == IdempotencyCreateOutcome.ExistingCompleted)
        {
            return Result.Success(
                ToSummary(
                    loaded.Value.ServiceDate,
                    loaded.Value.Availability,
                    includeClosedNeeds: true));
        }

        if (idempotency.Outcome is IdempotencyCreateOutcome.ExistingProcessing)
        {
            return Result.Failure<ServiceDateSummary>(
                DomainError.Conflict(
                    DateApplicationErrorCodes.IdempotencyInProgress,
                    "The date-open request is already processing."));
        }

        if (idempotency.Outcome is IdempotencyCreateOutcome.RequestMismatch)
        {
            return Result.Failure<ServiceDateSummary>(
                DomainError.Conflict(
                    DateApplicationErrorCodes.IdempotencyMismatch,
                    "The idempotency key was already used for a different request."));
        }

        if (idempotency.Outcome is IdempotencyCreateOutcome.ExistingFailed or IdempotencyCreateOutcome.Expired)
        {
            return Result.Failure<ServiceDateSummary>(
                DomainError.Conflict(
                    DateApplicationErrorCodes.IdempotencyMismatch,
                    "The idempotency key cannot be reused."));
        }

        string before = Serialize(loaded.Value.ServiceDate);
        Result<ServiceDateOpened> opened = loaded.Value.ServiceDate.Open(now);
        if (opened.IsFailure)
        {
            return Result.Failure<ServiceDateSummary>(opened.Error);
        }

        Result saved = await serviceDateRepository.SaveAsync(
            loaded.Value,
            Effects(
                "service_date.opened",
                loaded.Value.ServiceDate,
                currentActor.MembershipId,
                now,
                "Service date opened by the managing Food Incharge.",
                before,
                includeOutbox: true),
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
                OpenOperation,
                fingerprint),
            command.ServiceDateId.ToString(),
            cancellationToken);
        if (completed.Outcome != IdempotencyTransitionOutcome.Completed)
        {
            throw new InvalidOperationException("The date-open idempotency receipt could not be completed.");
        }

        return Result.Success(
            ToSummary(
                loaded.Value.ServiceDate,
                loaded.Value.Availability,
                includeClosedNeeds: true));
    }

    private async ValueTask<Result<LoadedServiceDate>> LoadManagedAsync(
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<LoadedServiceDate>(actor.Error);
        }

        Result<LoadedServiceDate> loaded = await serviceDateRepository.GetAsync(
            currentActor.OrganizationId,
            serviceDateId,
            cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded;
        }

        return actor.Value.Roles.Contains(OrganizationRole.FoodIncharge)
            && loaded.Value.ServiceDate.ManagerMembershipId == currentActor.MembershipId
            ? loaded
            : Result.Failure<LoadedServiceDate>(DateApplicationErrorCodes.Forbidden());
    }

    private async ValueTask<Result<ActiveMembershipContext>> RequireManagingFoodInchargeAsync(
        MembershipId managerMembershipId,
        CancellationToken cancellationToken)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor;
        }

        return actor.Value.Roles.Contains(OrganizationRole.FoodIncharge)
            && managerMembershipId == currentActor.MembershipId
            ? actor
            : Result.Failure<ActiveMembershipContext>(DateApplicationErrorCodes.Forbidden());
    }

    private ValueTask<Result<ActiveMembershipContext>> ResolveActorAsync(CancellationToken cancellationToken) =>
        membershipRepository.ResolveActiveActorAsync(
            currentActor.UserId,
            currentActor.MembershipId,
            currentActor.OrganizationId,
            cancellationToken);

    private static ServiceDateSummary ToSummary(
        ServiceDate date,
        IReadOnlyDictionary<HelpNeedId, int?> availability,
        bool includeClosedNeeds) =>
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
                .Where(need => includeClosedNeeds || need.Status == HelpNeedStatus.Open)
                .OrderBy(need => need.Category)
                .Select(
                    need => new HelpNeedSummary(
                        need.Id,
                        need.Category,
                        need.Instructions,
                        availability.GetValueOrDefault(need.Id, need.Capacity),
                        need.Status,
                        need.Version))
                .ToArray());

    private static HelpNeedSummary ToSummary(HelpNeed need) =>
        new(
            need.Id,
            need.Category,
            need.Instructions,
            need.Capacity,
            need.Status,
            need.Version);

    private static DatePersistenceEffects Effects(
        string action,
        ServiceDate date,
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt,
        string reason,
        string? beforeState,
        bool includeOutbox = false)
    {
        string afterState = Serialize(date);
        AuditEntry audit = new(
            AuditEventId.New(),
            date.OrganizationId,
            actorMembershipId,
            action,
            "service_date",
            date.Id.ToString(),
            reason,
            "date_management",
            date.Id.ToString(),
            beforeState,
            afterState,
            occurredAt);
        OutboxMessage[] outbox = includeOutbox
            ? [new OutboxMessage(
                OutboxMessageId.New(),
                date.OrganizationId,
                action,
                JsonSerializer.Serialize(new { serviceDateId = date.Id.ToString() }),
                occurredAt)]
            : [];
        return new DatePersistenceEffects([audit], outbox);
    }

    private static string Serialize(ServiceDate date) =>
        JsonSerializer.Serialize(
            new
            {
                id = date.Id.ToString(),
                status = date.Status,
                version = date.Version,
                needs = date.HelpNeeds.Select(need => new
                {
                    id = need.Id.ToString(),
                    category = need.Category,
                    status = need.Status,
                    version = need.Version,
                }),
            });

    private static RequestFingerprint Fingerprint(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return RequestFingerprint.FromSha256(Convert.ToHexString(hash));
    }
}
