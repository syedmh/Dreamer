using System.Reflection;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Application.Dates.Management;
using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Tests.Dates.Management;

public sealed class DateManagementServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EditNeedRejectsCapacityBelowApprovedParticipantsWithoutPersistenceSideEffects()
    {
        Fixture fixture = Fixture.Create(
            dateStatus: ServiceDateStatus.Open,
            needStatuses: [HelpNeedStatus.Open],
            aggregateBuilders:
            [
                static (fixture, dateId, needId) => CreateAggregate(
                    fixture.OrganizationId,
                    dateId,
                    needId,
                    capacity: 10,
                    signupStates:
                    [
                        new SignupState(SignupStatus.Approved, MembershipId.New(), UnnamedParticipants: 0),
                        new SignupState(SignupStatus.Approved, MembershipId.New(), UnnamedParticipants: 0),
                    ]),
            ]);

        Result<HelpNeedSummary> result = await fixture.Service.EditNeedAsync(
            new EditHelpNeedCommand(
                fixture.NeedIds[0],
                "Updated instructions",
                1,
                HelpNeedStatus.Open),
            expectedNeedVersion: 0);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.CapacityUnavailable, result.Error.Code);
        Assert.Equal(0, fixture.ServiceDateRepository.SaveNeedCalls);
        Assert.Equal(0, fixture.SignupRepository.SaveCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelCompletedReplayReturnsCurrentProjectionWithoutNewWritesOrCompletion()
    {
        Fixture fixture = Fixture.Create(
            dateStatus: ServiceDateStatus.Cancelled,
            needStatuses: [HelpNeedStatus.Closed],
            dateVersion: 3,
            needVersions: [1],
            aggregateBuilders:
            [
                static (fixture, dateId, needId) => CreateAggregate(
                    fixture.OrganizationId,
                    dateId,
                    needId,
                    capacity: 10,
                    signupStates: []),
            ],
            idempotencyOutcome: IdempotencyCreateOutcome.ExistingCompleted);

        Result<ServiceDateSummary> result = await fixture.Service.CancelAsync(
            new CancelServiceDateCommand(
                fixture.DateId,
                "Weather",
                fixture.IdempotencyKey),
            expectedDateVersion: 0);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(ServiceDateStatus.Cancelled, result.Value.Status);
        Assert.Equal(0, fixture.ServiceDateRepository.SaveCalls);
        Assert.Equal(0, fixture.SignupRepository.SaveCalls);
        Assert.Equal(0, fixture.IdempotencyStore.CompleteCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelDeduplicatesNotificationsAndAccessRevocationByPrimaryMembership()
    {
        MembershipId sharedPrimary = MembershipId.New();
        MembershipId pendingPrimary = MembershipId.New();
        Fixture fixture = Fixture.Create(
            dateStatus: ServiceDateStatus.Open,
            needStatuses: [HelpNeedStatus.Open, HelpNeedStatus.Open],
            dateVersion: 3,
            aggregateBuilders:
            [
                (fixture, dateId, needId) => CreateAggregate(
                    fixture.OrganizationId,
                    dateId,
                    needId,
                    capacity: 10,
                    signupStates:
                    [
                        new SignupState(SignupStatus.Approved, sharedPrimary, UnnamedParticipants: 0),
                    ]),
                (fixture, dateId, needId) => CreateAggregate(
                    fixture.OrganizationId,
                    dateId,
                    needId,
                    capacity: 10,
                    signupStates:
                    [
                        new SignupState(SignupStatus.Approved, sharedPrimary, UnnamedParticipants: 0),
                        new SignupState(SignupStatus.Pending, pendingPrimary, UnnamedParticipants: 0),
                    ]),
            ]);

        Result<ServiceDateSummary> result = await fixture.Service.CancelAsync(
            new CancelServiceDateCommand(
                fixture.DateId,
                "Weather",
                fixture.IdempotencyKey),
            expectedDateVersion: fixture.LoadedDate.LoadedVersion);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, fixture.NotificationWriter.Notifications.Count);
        Assert.Equal(
            new[] { pendingPrimary.ToString(), sharedPrimary.ToString() }.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            fixture.NotificationWriter.Notifications
                .Select(notification => notification.RecipientMembershipId.ToString())
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            2,
            fixture.OutboxWriter.Messages.Count(message => message.Type == "notification.push_requested"));
        Assert.Equal(
            1,
            fixture.OutboxWriter.Messages.Count(message => message.Type == "thread.access_changed"));
        Assert.Equal(3, fixture.AuditWriter.Entries.Count);
        Assert.Equal(0, fixture.SignupRepository.SaveCalls);
        Assert.Equal(2, fixture.SignupRepository.SaveDateCancellationCalls);
        Assert.Equal(1, fixture.ServiceDateRepository.SaveCalls);
        Assert.Equal(1, fixture.IdempotencyStore.CompleteCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task CancelClosedDatePersistsPreservedActiveSignupsViaDateCancellationSavePath()
    {
        MembershipId approvedPrimary = MembershipId.New();
        MembershipId pendingPrimary = MembershipId.New();
        MembershipId waitlistedPrimary = MembershipId.New();
        Fixture fixture = Fixture.Create(
            dateStatus: ServiceDateStatus.Closed,
            needStatuses: [HelpNeedStatus.Closed],
            dateVersion: 3,
            needVersions: [1],
            aggregateBuilders:
            [
                (fixture, dateId, needId) => CreateAggregate(
                    fixture.OrganizationId,
                    dateId,
                    needId,
                    capacity: 10,
                    signupStates:
                    [
                        new SignupState(SignupStatus.Approved, approvedPrimary, UnnamedParticipants: 0),
                        new SignupState(SignupStatus.Pending, pendingPrimary, UnnamedParticipants: 0),
                        new SignupState(SignupStatus.Waitlisted, waitlistedPrimary, UnnamedParticipants: 0),
                    ],
                    serviceDateStatus: ServiceDateStatus.Closed,
                    helpNeedStatus: HelpNeedStatus.Closed,
                    dateVersion: 3,
                    needVersion: 1),
            ]);

        Result<ServiceDateSummary> result = await fixture.Service.CancelAsync(
            new CancelServiceDateCommand(
                fixture.DateId,
                "Weather",
                fixture.IdempotencyKey),
            expectedDateVersion: fixture.LoadedDate.LoadedVersion);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(ServiceDateStatus.Cancelled, result.Value.Status);
        Assert.Equal(0, fixture.SignupRepository.SaveCalls);
        Assert.Equal(1, fixture.SignupRepository.SaveDateCancellationCalls);
        Assert.Equal(3, fixture.NotificationWriter.Notifications.Count);
        Assert.Equal(3, fixture.AuditWriter.Entries.Count);
        Assert.Equal(
            3,
            fixture.OutboxWriter.Messages.Count(message => message.Type == "notification.push_requested"));
        Assert.Equal(1, fixture.ServiceDateRepository.SaveCalls);
        Assert.Equal(1, fixture.IdempotencyStore.CompleteCalls);
        Assert.Equal(1, fixture.UnitOfWork.CommitCount);
    }

    private static HelpNeedSignups CreateAggregate(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        HelpNeedId helpNeedId,
        int? capacity,
        IReadOnlyCollection<SignupState> signupStates,
        ServiceDateStatus serviceDateStatus = ServiceDateStatus.Open,
        HelpNeedStatus helpNeedStatus = HelpNeedStatus.Open,
        long dateVersion = 2,
        long needVersion = 0)
    {
        HelpNeed workingNeed = HelpNeed.Rehydrate(
            helpNeedId,
            serviceDateId,
            HelpCategory.FoodPreparation,
            "Chop vegetables.",
            capacity,
            HelpNeedStatus.Open,
            0).Value;
        ServiceDate workingDate = ServiceDate.Rehydrate(
            serviceDateId,
            organizationId,
            "Service date",
            "Prepare food.",
            Now.AddDays(1),
            Now.AddDays(1).AddHours(4),
            Now.AddHours(23),
            MembershipId.New(),
            ServiceDateStatus.Open,
            2,
            [workingNeed]).Value;

        HelpNeedSignups aggregate = Rehydrate(workingDate, workingNeed, 0, 0, []);
        int sequence = 0;
        foreach (SignupState state in signupStates)
        {
            SignupId signupId = SignupId.New();
            Assert.True(
                aggregate.Submit(
                    signupId,
                    ActiveMembership(state.PrimaryMembershipId, organizationId),
                    SignupKind.Individual,
                    [],
                    state.UnnamedParticipants,
                    Now.AddMinutes(sequence)).IsSuccess);
            if (state.Status == SignupStatus.Approved)
            {
                Assert.True(aggregate.Approve(signupId, Now.AddMinutes(sequence + 1)).IsSuccess);
            }
            else if (state.Status == SignupStatus.Waitlisted)
            {
                Assert.True(aggregate.Waitlist(signupId, Now.AddMinutes(sequence + 1)).IsSuccess);
            }

            sequence += 2;
        }

        HelpNeed finalNeed = HelpNeed.Rehydrate(
            helpNeedId,
            serviceDateId,
            HelpCategory.FoodPreparation,
            "Chop vegetables.",
            capacity,
            helpNeedStatus,
            needVersion).Value;
        ServiceDate finalDate = ServiceDate.Rehydrate(
            serviceDateId,
            organizationId,
            "Service date",
            "Prepare food.",
            Now.AddDays(1),
            Now.AddDays(1).AddHours(4),
            Now.AddHours(23),
            MembershipId.New(),
            serviceDateStatus,
            dateVersion,
            [finalNeed]).Value;
        return Rehydrate(finalDate, finalNeed, aggregate.Version, aggregate.WaitlistOrderHighWater, aggregate.Signups);
    }

    private static Membership ActiveMembership(
        MembershipId membershipId,
        OrganizationId organizationId)
    {
        Result<Membership> membership = Membership.Invite(
            membershipId,
            organizationId,
            UserId.New(),
            membershipId.ToString(),
            eligibleAsNamedParticipant: true);
        Assert.True(membership.IsSuccess, membership.IsFailure ? membership.Error.Message : null);
        Assert.True(membership.Value.Activate(DateTimeOffset.UnixEpoch).IsSuccess);
        return membership.Value;
    }

    private static HelpNeedSignups Rehydrate(
        ServiceDate date,
        HelpNeed need,
        long version,
        long waitlistOrderHighWater,
        IReadOnlyCollection<Signup> signups)
    {
        Type factory = typeof(HelpNeedSignups).GetNestedType(
            "PersistenceFactory",
            BindingFlags.NonPublic)!
            ?? throw new InvalidOperationException("Missing persistence factory.");
        MethodInfo method = factory.GetMethod(
            "Rehydrate",
            BindingFlags.Static | BindingFlags.NonPublic)!
            ?? throw new InvalidOperationException("Missing aggregate rehydrate method.");
        object result = method.Invoke(null, [date, need, version, waitlistOrderHighWater, signups])!;
        return (HelpNeedSignups)result.GetType().GetProperty("Value")!.GetValue(result)!;
    }

    private sealed record SignupState(
        SignupStatus Status,
        MembershipId PrimaryMembershipId,
        int UnnamedParticipants);

    private sealed class Fixture
    {
        private Fixture(
            RecordingUnitOfWork unitOfWork,
            StubServiceDateRepository serviceDateRepository,
            StubSignupRepository signupRepository,
            StubThreadRepository threadRepository,
            RecordingNotificationWriter notificationWriter,
            RecordingAuditWriter auditWriter,
            RecordingOutboxWriter outboxWriter,
            StubMembershipRepository membershipRepository,
            StubIdempotencyStore idempotencyStore,
            StubCurrentActor currentActor,
            LoadedServiceDate loadedDate,
            IReadOnlyList<HelpNeedId> needIds)
        {
            UnitOfWork = unitOfWork;
            ServiceDateRepository = serviceDateRepository;
            SignupRepository = signupRepository;
            ThreadRepository = threadRepository;
            NotificationWriter = notificationWriter;
            AuditWriter = auditWriter;
            OutboxWriter = outboxWriter;
            MembershipRepository = membershipRepository;
            IdempotencyStore = idempotencyStore;
            CurrentActor = currentActor;
            LoadedDate = loadedDate;
            NeedIds = needIds;
            DateId = loadedDate.ServiceDate.Id;
            IdempotencyKey = IdempotencyKey.New();
            Service = new DateManagementService(
                unitOfWork,
                serviceDateRepository,
                signupRepository,
                threadRepository,
                notificationWriter,
                auditWriter,
                outboxWriter,
                membershipRepository,
                idempotencyStore,
                currentActor,
                new FixedClock(Now.AddHours(1)));
        }

        public static Fixture Create(
            ServiceDateStatus dateStatus,
            IReadOnlyList<HelpNeedStatus> needStatuses,
            IReadOnlyList<Func<FixtureSeed, ServiceDateId, HelpNeedId, HelpNeedSignups>> aggregateBuilders,
            long dateVersion = 2,
            IReadOnlyList<long>? needVersions = null,
            IdempotencyCreateOutcome idempotencyOutcome = IdempotencyCreateOutcome.Created)
        {
            OrganizationId organizationId = OrganizationId.New();
            MembershipId managerMembershipId = MembershipId.New();
            ServiceDateId dateId = ServiceDateId.New();
            List<HelpNeed> needs = [];
            List<HelpNeedId> needIds = [];
            for (int index = 0; index < needStatuses.Count; index++)
            {
                HelpNeedId needId = HelpNeedId.New();
                needIds.Add(needId);
                needs.Add(
                    HelpNeed.Rehydrate(
                        needId,
                        dateId,
                        (HelpCategory)index,
                        "Chop vegetables.",
                        10,
                        needStatuses[index],
                        needVersions?[index] ?? (needStatuses[index] == HelpNeedStatus.Closed ? 1 : 0)).Value);
            }

            ServiceDate date = ServiceDate.Rehydrate(
                dateId,
                organizationId,
                "Service date",
                "Prepare food.",
                Now.AddDays(1),
                Now.AddDays(1).AddHours(4),
                Now.AddHours(23),
                managerMembershipId,
                dateStatus,
                dateVersion,
                needs).Value;
            LoadedServiceDate loadedDate = new(
                date,
                dateVersion,
                needIds.ToDictionary(id => id, _ => (int?)10),
                needs.ToDictionary(need => need.Id, need => need.Version));

            FixtureSeed seed = new(organizationId, managerMembershipId);
            Dictionary<HelpNeedId, HelpNeedSignups> aggregates = [];
            for (int index = 0; index < needIds.Count; index++)
            {
                aggregates[needIds[index]] = aggregateBuilders[index](seed, dateId, needIds[index]);
            }

            StubCurrentActor currentActor = new(UserId.New(), managerMembershipId, organizationId);
            StubMembershipRepository membershipRepository = new(
                new ActiveMembershipContext(
                    currentActor.UserId,
                    currentActor.MembershipId,
                    currentActor.OrganizationId,
                    "Manager",
                    true,
                    [OrganizationRole.FoodIncharge],
                    "Organization",
                    "America/Los_Angeles"));
            return new Fixture(
                new RecordingUnitOfWork(),
                new StubServiceDateRepository(loadedDate),
                new StubSignupRepository(
                    aggregates,
                    dateStatus == ServiceDateStatus.Open,
                    needIds
                        .Where((_, index) => needStatuses[index] == HelpNeedStatus.Open)
                        .ToHashSet()),
                new StubThreadRepository(),
                new RecordingNotificationWriter(),
                new RecordingAuditWriter(),
                new RecordingOutboxWriter(),
                membershipRepository,
                new StubIdempotencyStore(idempotencyOutcome, dateId.ToString()),
                currentActor,
                loadedDate,
                needIds);
        }

        public RecordingUnitOfWork UnitOfWork { get; }
        public StubServiceDateRepository ServiceDateRepository { get; }
        public StubSignupRepository SignupRepository { get; }
        public StubThreadRepository ThreadRepository { get; }
        public RecordingNotificationWriter NotificationWriter { get; }
        public RecordingAuditWriter AuditWriter { get; }
        public RecordingOutboxWriter OutboxWriter { get; }
        public StubMembershipRepository MembershipRepository { get; }
        public StubIdempotencyStore IdempotencyStore { get; }
        public StubCurrentActor CurrentActor { get; }
        public LoadedServiceDate LoadedDate { get; }
        public IReadOnlyList<HelpNeedId> NeedIds { get; }
        public ServiceDateId DateId { get; }
        public IdempotencyKey IdempotencyKey { get; }
        public DateManagementService Service { get; }
    }

    private sealed record FixtureSeed(
        OrganizationId OrganizationId,
        MembershipId ManagerMembershipId);

    private sealed class StubCurrentActor(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId) : ICurrentActor
    {
        public UserId UserId { get; } = userId;
        public MembershipId MembershipId { get; } = membershipId;
        public OrganizationId OrganizationId { get; } = organizationId;
    }

    private sealed class FixedClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow => value;
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int CommitCount { get; private set; }

        public async ValueTask<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<Result<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            Result<T> result = await operation(cancellationToken);
            if (result.IsSuccess)
            {
                CommitCount++;
            }

            return result;
        }
    }

    private sealed class StubServiceDateRepository(LoadedServiceDate loaded) : IServiceDateRepository
    {
        public int SaveCalls { get; private set; }
        public int SaveNeedCalls { get; private set; }

        public ValueTask<Result> CreateAsync(ServiceDate serviceDate, DatePersistenceEffects effects, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<LoadedServiceDate>> GetAsync(OrganizationId organizationId, ServiceDateId serviceDateId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(loaded));

        public ValueTask<Result<LoadedServiceDate>> GetByHelpNeedAsync(OrganizationId organizationId, HelpNeedId helpNeedId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(loaded));

        public ValueTask<Result> RevalidateManagedAuthorityAsync(OrganizationId organizationId, ServiceDateId serviceDateId, MembershipId actorMembershipId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> SaveAsync(LoadedServiceDate loadedDate, DatePersistenceEffects effects, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> SaveNeedAsync(LoadedServiceDate loadedDate, HelpNeedId helpNeedId, long expectedHelpNeedVersion, long? expectedSignupVersion, DatePersistenceEffects effects, CancellationToken cancellationToken = default)
        {
            SaveNeedCalls++;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result<OpenServiceDatesPage>> ListOpenAsync(OrganizationId organizationId, string? cursor, int? pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubSignupRepository(
        Dictionary<HelpNeedId, HelpNeedSignups> aggregates,
        bool serviceDateOpen,
        HashSet<HelpNeedId> openNeedIds) : ISignupRepository
    {
        public int SaveCalls { get; private set; }
        public int SaveDateCancellationCalls { get; private set; }

        public ValueTask<Result<HelpNeedSignups>> GetAsync(OrganizationId organizationId, HelpNeedId helpNeedId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(aggregates[helpNeedId]));

        public ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextAsync(OrganizationId organizationId, HelpNeedId helpNeedId, MembershipId primaryMembershipId, IReadOnlyCollection<MembershipId> memberParticipantIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> SaveAsync(HelpNeedSignups aggregate, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return ValueTask.FromResult(
                serviceDateOpen && openNeedIds.Contains(aggregate.HelpNeedId)
                    ? Result.Success()
                    : Result.Failure(
                        DomainError.Conflict(
                            ErrorCodes.CategoryClosed,
                            "The service date and help need must be open before signup changes are saved.")));
        }

        public ValueTask<Result> SaveDateCancellationAsync(
            HelpNeedSignups aggregate,
            MembershipId actorMembershipId,
            CancellationToken cancellationToken = default)
        {
            SaveDateCancellationCalls++;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> SaveSubmissionAsync(HelpNeedSignups aggregate, SignupSubmissionWrite write, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<SignupSummary>> GetOwnedAsync(OrganizationId organizationId, MembershipId primaryMembershipId, SignupId signupId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<SignupPage>> ListMineAsync(OrganizationId organizationId, MembershipId primaryMembershipId, string? cursor, int? pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<RosterPage>> GetManagedRosterAsync(OrganizationId organizationId, ServiceDateId serviceDateId, MembershipId actorMembershipId, string? cursor, int? pageSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubThreadRepository : IThreadRepository
    {
        public int SaveCalls { get; private set; }

        public ValueTask<Result<LoadedDateThread>> GetAsync(OrganizationId organizationId, ThreadId threadId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure<LoadedDateThread>(DomainError.NotFound("thread_not_found", "Not found.")));

        public ValueTask<Result<LoadedDateThread>> GetByServiceDateAsync(OrganizationId organizationId, ServiceDateId serviceDateId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Failure<LoadedDateThread>(DomainError.NotFound("thread_not_found", "Not found.")));

        public ValueTask<Result> SaveAsync(LoadedDateThread loaded, ThreadPersistenceEffects effects, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class RecordingNotificationWriter : INotificationWriter
    {
        public List<Notification> Notifications { get; } = [];

        public ValueTask AddAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOutboxWriter : IOutboxWriter
    {
        public List<OutboxMessage> Messages { get; } = [];

        public ValueTask AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubMembershipRepository(ActiveMembershipContext actor) : IMembershipRepository
    {
        public ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextAsync(string invitationToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> AcceptInvitationAsync(string invitationToken, MembershipId membershipId, string displayName, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(OrganizationId organizationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> SaveGovernanceAsync(OrganizationAccountGovernance governance, MembershipId actorMembershipId, DateTimeOffset occurredAt, MembershipAdministrationPersistenceEffects effects, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> IssueInvitationAsync(IssueMembershipInvitationPersistenceRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(UserId userId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(actor));

        public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(UserId userId, MembershipId membershipId, OrganizationId organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(actor));
    }

    private sealed class StubIdempotencyStore(
        IdempotencyCreateOutcome createOutcome,
        string resultReference) : IIdempotencyStore
    {
        public int CompleteCalls { get; private set; }

        public ValueTask<IdempotencyReceipt?> FindAsync(OrganizationId organizationId, MembershipId membershipId, IdempotencyKey key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IdempotencyCreateResult> TryCreateProcessingAsync(IdempotencyCreateRequest request, CancellationToken cancellationToken = default)
        {
            IdempotencyStatus status = createOutcome == IdempotencyCreateOutcome.ExistingCompleted
                ? IdempotencyStatus.Completed
                : IdempotencyStatus.Processing;
            IdempotencyReceipt receipt = new(
                request.OrganizationId,
                request.MembershipId,
                request.Key,
                request.Operation,
                request.RequestFingerprint,
                status,
                status == IdempotencyStatus.Completed ? resultReference : null,
                request.CreatedAt,
                request.ExpiresAt);
            return ValueTask.FromResult(new IdempotencyCreateResult(createOutcome, receipt));
        }

        public ValueTask<IdempotencyTransitionResult> TryCompleteAsync(IdempotencyRequest request, string resultReference, CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            return ValueTask.FromResult(
                new IdempotencyTransitionResult(
                    IdempotencyTransitionOutcome.Completed,
                    new IdempotencyReceipt(
                        request.OrganizationId,
                        request.MembershipId,
                        request.Key,
                        request.Operation,
                        request.RequestFingerprint,
                        IdempotencyStatus.Completed,
                        resultReference,
                        Now,
                        Now.AddHours(24))));
        }

        public ValueTask<IdempotencyTransitionResult> TryFailAsync(IdempotencyRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
