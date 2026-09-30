using System.Reflection;
using HusayniaTabruk.Application.Abstractions;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Application.Threads;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Application.Tests.Threads;

public sealed class ThreadServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListRevalidatesLiveManagerRoleOnEveryRequest()
    {
        Fixture fixture = Fixture.Create();

        Result<ThreadMessagePage> allowed = await fixture.Service.ListAsync(
            fixture.ServiceDateId,
            cursor: null,
            pageSize: null);

        fixture.MembershipRepository.Actor = fixture.MembershipRepository.Actor with
        {
            Roles = [],
        };
        Result<ThreadMessagePage> denied = await fixture.Service.ListAsync(
            fixture.ServiceDateId,
            cursor: null,
            pageSize: null);

        Assert.True(allowed.IsSuccess, allowed.IsFailure ? allowed.Error.Message : null);
        Assert.True(denied.IsFailure);
        Assert.Equal(ErrorType.NotFound, denied.Error.Type);
        Assert.Equal(ThreadErrorCodes.ThreadAccessDenied, denied.Error.Code);
        Assert.Equal(2, fixture.ThreadRepository.OrdinaryAuthorizationCalls);
        Assert.Equal(0, fixture.MembershipRepository.ResolveCalls);
    }

    [Fact]
    public async Task ListRevalidatesLiveApprovedPrimarySignupOnEveryRequest()
    {
        OrganizationId organizationId = OrganizationId.New();
        MembershipId participantId = MembershipId.New();
        MembershipId managerId = MembershipId.New();
        UserId participantUserId = UserId.New();
        ServiceDateId serviceDateId = ServiceDateId.New();
        HelpNeedId helpNeedId = HelpNeedId.New();
        HelpNeed need = HelpNeed.Rehydrate(
            helpNeedId,
            serviceDateId,
            HelpCategory.FoodPreparation,
            "Prepare food",
            capacity: 10,
            HelpNeedStatus.Open,
            version: 0).Value;
        ServiceDate date = ServiceDate.Rehydrate(
            serviceDateId,
            organizationId,
            "Service date",
            "Prepare meals",
            Now.AddDays(1),
            Now.AddDays(1).AddHours(4),
            Now.AddHours(23),
            managerId,
            ServiceDateStatus.Open,
            version: 2,
            [need]).Value;
        HelpNeedSignups signups = RehydrateSignups(date, need);
        Membership participant = Membership.Rehydrate(
            participantId,
            organizationId,
            participantUserId,
            "Participant",
            MembershipStatus.Active,
            eligibleAsNamedParticipant: true,
            activeRoles: []).Value;
        SignupId signupId = SignupId.New();
        Assert.True(
            signups.Submit(
                signupId,
                participant,
                SignupKind.Individual,
                [],
                unnamedParticipantCount: 0,
                Now).IsSuccess);
        Assert.True(signups.Approve(signupId, Now.AddMinutes(1)).IsSuccess);
        Membership manager = Membership.Rehydrate(
            managerId,
            organizationId,
            UserId.New(),
            "Manager",
            MembershipStatus.Active,
            eligibleAsNamedParticipant: true,
            [OrganizationRole.FoodIncharge]).Value;
        DateThread thread = DateThread.Create(
            ThreadId.New(),
            organizationId,
            serviceDateId).Value;
        Assert.True(
            thread.Post(
                MessageId.New(),
                IdempotencyKey.New(),
                "Visible to participant",
                manager,
                date,
                primaryContactSignup: null,
                Now).IsSuccess);

        RecordingUnitOfWork unitOfWork = new();
        FakeMembershipRepository memberships = new(
            new ActiveMembershipContext(
                participantUserId,
                participantId,
                organizationId,
                "Participant",
                EligibleAsNamedParticipant: true,
                Roles: [],
                "Organization",
                "UTC"));
        FakeSignupRepository signupRepository = new(signups);
        FakeThreadRepository threadRepository = new(
            new LoadedDateThread(thread, thread.Version),
            requirement =>
            {
                Signup? approved = signups.Signups.SingleOrDefault(
                    signup => signup.PrimaryMembershipId == participantId
                        && signup.Status == SignupStatus.Approved);
                return requirement == ThreadAuthorizationRequirement.ParticipantOrManager
                    && approved is not null
                    ? Result.Success(
                        new ThreadOrdinaryAuthorizationContext(
                            participant,
                            date,
                            approved))
                    : Result.Failure<ThreadOrdinaryAuthorizationContext>(
                        ThreadErrorCodes.Concealed());
            },
            () => Result.Failure<ThreadPrivilegedAuthorizationContext>(
                DomainError.Forbidden("forbidden", "Forbidden.")));
        ThreadService service = new(
            unitOfWork,
            new FakeServiceDateRepository(date),
            signupRepository,
            threadRepository,
            memberships,
            new RecordingPrivilegedAccessWriter(fails: false),
            new RecordingStepUpVerifier(),
            new CurrentActor(participantUserId, participantId, organizationId),
            new FixedClock(Now.AddMinutes(5)));

        Result<ThreadMessagePage> allowed = await service.ListAsync(
            serviceDateId,
            cursor: null,
            pageSize: null);
        Assert.True(signups.Withdraw(signupId, Now.AddMinutes(2)).IsSuccess);
        Result<ThreadMessagePage> denied = await service.ListAsync(
            serviceDateId,
            cursor: null,
            pageSize: null);

        Assert.True(allowed.IsSuccess, allowed.IsFailure ? allowed.Error.Message : null);
        Assert.True(denied.IsFailure);
        Assert.Equal(ErrorType.NotFound, denied.Error.Type);
    }

    [Fact]
    public async Task StalePostAllowsOnlyExactPersistedReplayWithoutSaving()
    {
        IdempotencyKey key = IdempotencyKey.New();
        Fixture fixture = Fixture.Create(
            seed: (thread, actor, date) =>
            {
                Result<MessagePosted> posted = thread.Post(
                    MessageId.New(),
                    key,
                    "Already stored",
                    actor,
                    date,
                    primaryContactSignup: null,
                    Now);
                Assert.True(posted.IsSuccess);
            });

        Result<VersionedThreadMessage> replay = await fixture.Service.PostAsync(
            new PostThreadMessageCommand(
                fixture.ServiceDateId,
                "Already stored",
                key),
            expectedVersion: 0);
        Result<VersionedThreadMessage> mismatch = await fixture.Service.PostAsync(
            new PostThreadMessageCommand(
                fixture.ServiceDateId,
                "Changed body",
                key),
            expectedVersion: 0);
        fixture.MembershipRepository.Actor = fixture.MembershipRepository.Actor with
        {
            Roles = [],
        };
        Result<VersionedThreadMessage> noLongerAuthorized =
            await fixture.Service.PostAsync(
                new PostThreadMessageCommand(
                    fixture.ServiceDateId,
                    "Already stored",
                    key),
                expectedVersion: 0);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);
        Assert.Equal(1, replay.Value.Version);
        Assert.True(mismatch.IsFailure);
        Assert.Equal(ErrorType.PreconditionFailed, mismatch.Error.Type);
        Assert.True(noLongerAuthorized.IsFailure);
        Assert.Equal(ErrorType.NotFound, noLongerAuthorized.Error.Type);
        Assert.Equal(0, fixture.ThreadRepository.SaveCalls);
    }

    [Fact]
    public async Task DuplicateReportWithDifferentNormalizedContentReturnsConflict()
    {
        MessageId messageId = MessageId.New();
        Fixture fixture = Fixture.Create(
            seed: (thread, actor, date) =>
            {
                Assert.True(
                    thread.Post(
                        messageId,
                        IdempotencyKey.New(),
                        "Please review",
                        actor,
                        date,
                        primaryContactSignup: null,
                        Now).IsSuccess);
                Assert.True(
                    thread.Report(
                        messageId,
                        MessageReportReason.Spam,
                        "same",
                        actor,
                        date,
                        primaryContactSignup: null,
                        Now.AddMinutes(1)).IsSuccess);
            });

        Result<VersionedThreadReport> result = await fixture.Service.ReportAsync(
            new ReportThreadMessageCommand(
                fixture.ServiceDateId,
                messageId,
                MessageReportReason.Spam,
                "different"),
            expectedVersion: 2);
        Result<VersionedThreadReport> stale = await fixture.Service.ReportAsync(
            new ReportThreadMessageCommand(
                fixture.ServiceDateId,
                messageId,
                MessageReportReason.Spam,
                "different"),
            expectedVersion: 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        Assert.Equal(ThreadApplicationErrorCodes.DuplicateReportMismatch, result.Error.Code);
        Assert.True(stale.IsFailure);
        Assert.Equal(ErrorType.PreconditionFailed, stale.Error.Type);
        Assert.Equal(0, fixture.ThreadRepository.SaveCalls);
    }

    [Fact]
    public async Task PrivilegedReadAuditFailureReturnsNoPageAndDoesNotCommit()
    {
        Fixture fixture = Fixture.Create(
            actorRoles: [OrganizationRole.Admin],
            auditFails: true,
            seed: (thread, actor, date) =>
            {
                Assert.True(
                    thread.Post(
                        MessageId.New(),
                        IdempotencyKey.New(),
                        "Sensitive stored body",
                        actor,
                        date,
                        primaryContactSignup: null,
                        Now).IsSuccess);
            });

        Result<PrivilegedThreadMessagePage> result =
            await fixture.Service.ReadPrivilegedAsync(
                new ReadPrivilegedThreadPageCommand(
                    fixture.ServiceDateId,
                    "Investigating an abuse report",
                    PrivilegedAccessPurpose.Moderation,
                    "CASE-123",
                    Cursor: null,
                    PageSize: null),
                new StepUpToken("step-up-token"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.DependencyUnavailable, result.Error.Type);
        Assert.Equal(1, fixture.StepUpVerifier.ConsumeCalls);
        Assert.Equal(1, fixture.PrivilegedAccessWriter.WriteCalls);
        Assert.Equal(1, fixture.ThreadRepository.PrivilegedAuthorizationCalls);
        Assert.Equal(0, fixture.MembershipRepository.ResolveCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task PrivilegedReadValidatesPrintableAsciiCaseIdBeforeConsumingStepUp()
    {
        Fixture fixture = Fixture.Create(actorRoles: [OrganizationRole.Admin]);

        Result<PrivilegedThreadMessagePage> result =
            await fixture.Service.ReadPrivilegedAsync(
                new ReadPrivilegedThreadPageCommand(
                    fixture.ServiceDateId,
                    "Support investigation",
                    PrivilegedAccessPurpose.Support,
                    "CASE-\n123",
                    Cursor: null,
                    PageSize: null),
                new StepUpToken("step-up-token"));

        Assert.True(result.IsFailure);
        Assert.Equal(ThreadApplicationErrorCodes.InvalidThreadRequest, result.Error.Code);
        Assert.Equal(0, fixture.StepUpVerifier.ConsumeCalls);
        Assert.Equal(0, fixture.UnitOfWork.CommitCount);
    }

    private sealed class Fixture
    {
        private Fixture(
            ServiceDateId serviceDateId,
            ThreadService service,
            RecordingUnitOfWork unitOfWork,
            FakeMembershipRepository membershipRepository,
            FakeThreadRepository threadRepository,
            RecordingPrivilegedAccessWriter privilegedAccessWriter,
            RecordingStepUpVerifier stepUpVerifier)
        {
            ServiceDateId = serviceDateId;
            Service = service;
            UnitOfWork = unitOfWork;
            MembershipRepository = membershipRepository;
            ThreadRepository = threadRepository;
            PrivilegedAccessWriter = privilegedAccessWriter;
            StepUpVerifier = stepUpVerifier;
        }

        public ServiceDateId ServiceDateId { get; }
        public ThreadService Service { get; }
        public RecordingUnitOfWork UnitOfWork { get; }
        public FakeMembershipRepository MembershipRepository { get; }
        public FakeThreadRepository ThreadRepository { get; }
        public RecordingPrivilegedAccessWriter PrivilegedAccessWriter { get; }
        public RecordingStepUpVerifier StepUpVerifier { get; }

        public static Fixture Create(
            IReadOnlyCollection<OrganizationRole>? actorRoles = null,
            bool auditFails = false,
            Action<DateThread, Membership, ServiceDate>? seed = null)
        {
            OrganizationId organizationId = OrganizationId.New();
            MembershipId membershipId = MembershipId.New();
            UserId userId = UserId.New();
            ServiceDateId serviceDateId = ServiceDateId.New();
            ThreadId threadId = ThreadId.New();
            IReadOnlyCollection<OrganizationRole> roles =
                actorRoles ?? [OrganizationRole.FoodIncharge];
            ActiveMembershipContext actor = new(
                userId,
                membershipId,
                organizationId,
                "Thread Actor",
                EligibleAsNamedParticipant: true,
                roles,
                "Organization",
                "UTC");
            Membership actorMembership = Membership.Rehydrate(
                membershipId,
                organizationId,
                userId,
                actor.DisplayName,
                MembershipStatus.Active,
                actor.EligibleAsNamedParticipant,
                roles.Append(OrganizationRole.FoodIncharge).Distinct().ToArray()).Value;
            ServiceDate date = ServiceDate.Create(
                serviceDateId,
                organizationId,
                "Service date",
                "Prepare meals",
                Now.AddDays(1),
                Now.AddDays(1).AddHours(4),
                Now.AddHours(23),
                membershipId).Value;
            Assert.True(date.Open(Now).IsSuccess);
            DateThread thread = DateThread.Create(
                threadId,
                organizationId,
                serviceDateId).Value;
            seed?.Invoke(thread, actorMembership, date);

            RecordingUnitOfWork unitOfWork = new();
            FakeMembershipRepository memberships = new(actor);
            FakeThreadRepository threads = new(
                new LoadedDateThread(thread, thread.Version),
                requirement =>
                {
                    ActiveMembershipContext current = memberships.Actor;
                    Result<Membership> currentMembership = Membership.Rehydrate(
                        current.MembershipId,
                        current.OrganizationId,
                        current.UserId,
                        current.DisplayName,
                        MembershipStatus.Active,
                        current.EligibleAsNamedParticipant,
                        current.Roles);
                    bool manager = currentMembership.IsSuccess
                        && current.Roles.Contains(OrganizationRole.FoodIncharge);
                    return manager
                        ? Result.Success(
                            new ThreadOrdinaryAuthorizationContext(
                                currentMembership.Value,
                                date,
                                ApprovedPrimarySignup: null))
                        : Result.Failure<ThreadOrdinaryAuthorizationContext>(
                            ThreadErrorCodes.Concealed());
                },
                () =>
                {
                    ActiveMembershipContext current = memberships.Actor;
                    return current.Roles.Contains(OrganizationRole.Admin)
                        ? Result.Success(
                            new ThreadPrivilegedAuthorizationContext(
                                current.UserId,
                                current.MembershipId,
                                current.OrganizationId))
                        : Result.Failure<ThreadPrivilegedAuthorizationContext>(
                            DomainError.Forbidden("forbidden", "Forbidden."));
                });
            RecordingPrivilegedAccessWriter privilegedWriter = new(auditFails);
            RecordingStepUpVerifier stepUpVerifier = new();
            CurrentActor currentActor = new(userId, membershipId, organizationId);
            ThreadService service = new(
                unitOfWork,
                new FakeServiceDateRepository(date),
                new FakeSignupRepository(),
                threads,
                memberships,
                privilegedWriter,
                stepUpVerifier,
                currentActor,
                new FixedClock(Now.AddMinutes(5)));
            return new Fixture(
                serviceDateId,
                service,
                unitOfWork,
                memberships,
                threads,
                privilegedWriter,
                stepUpVerifier);
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int CommitCount { get; private set; }

        public async ValueTask<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<Result<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            try
            {
                Result<T> result = await operation(cancellationToken);
                if (result.IsSuccess)
                {
                    CommitCount++;
                }

                return result;
            }
            catch (DependencyUnavailableException)
            {
                return Result.Failure<T>(
                    DomainError.DependencyUnavailable(
                        "dependency_unavailable",
                        "A required dependency is temporarily unavailable."));
            }
        }
    }

    private sealed class FakeServiceDateRepository(ServiceDate date) : IServiceDateRepository
    {
        public ValueTask<Result> CreateAsync(
            ServiceDate serviceDate,
            DatePersistenceEffects effects,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<LoadedServiceDate>> GetAsync(
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                organizationId == date.OrganizationId && serviceDateId == date.Id
                    ? Result.Success(new LoadedServiceDate(date, date.Version, new Dictionary<HelpNeedId, int?>(), new Dictionary<HelpNeedId, long>()))
                    : Result.Failure<LoadedServiceDate>(DomainError.NotFound("missing", "Missing.")));

        public ValueTask<Result> RevalidateManagedAuthorityAsync(
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            MembershipId actorMembershipId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> SaveAsync(
            LoadedServiceDate loaded,
            DatePersistenceEffects effects,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<OpenServiceDatesPage>> ListOpenAsync(
            OrganizationId organizationId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static HelpNeedSignups RehydrateSignups(
        ServiceDate date,
        HelpNeed need)
    {
        Type factory = typeof(HelpNeedSignups).GetNestedType(
            "PersistenceFactory",
            BindingFlags.NonPublic)!
            ?? throw new InvalidOperationException("Missing persistence factory.");
        MethodInfo method = factory.GetMethod(
            "Rehydrate",
            BindingFlags.Static | BindingFlags.NonPublic)!
            ?? throw new InvalidOperationException("Missing rehydrate method.");
        object result = method.Invoke(null, [date, need, 0L, 0L, Array.Empty<Signup>()])!;
        return (HelpNeedSignups)result.GetType().GetProperty("Value")!.GetValue(result)!;
    }

    private sealed class FakeSignupRepository(HelpNeedSignups? aggregate = null) : ISignupRepository
    {
        public ValueTask<Result<RosterPage>> GetManagedRosterAsync(
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            MembershipId managerMembershipId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<HelpNeedSignups>> GetAsync(
            OrganizationId organizationId,
            HelpNeedId helpNeedId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                aggregate is not null
                    && aggregate.OrganizationId == organizationId
                    && aggregate.HelpNeedId == helpNeedId
                    ? Result.Success(aggregate)
                    : Result.Failure<HelpNeedSignups>(
                        DomainError.NotFound("missing", "Missing.")));

        public ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextAsync(
            OrganizationId organizationId,
            HelpNeedId helpNeedId,
            MembershipId primaryMembershipId,
            IReadOnlyCollection<MembershipId> memberParticipantIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> SaveSubmissionAsync(
            HelpNeedSignups aggregate,
            SignupSubmissionWrite write,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<SignupSummary>> GetOwnedAsync(
            OrganizationId organizationId,
            MembershipId primaryMembershipId,
            SignupId signupId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<SignupPage>> ListMineAsync(
            OrganizationId organizationId,
            MembershipId primaryMembershipId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeThreadRepository(
        LoadedDateThread loaded,
        Func<ThreadAuthorizationRequirement, Result<ThreadOrdinaryAuthorizationContext>>
            ordinaryAuthorization,
        Func<Result<ThreadPrivilegedAuthorizationContext>> privilegedAuthorization)
        : IThreadRepository
    {
        public int SaveCalls { get; private set; }
        public int OrdinaryAuthorizationCalls { get; private set; }
        public int PrivilegedAuthorizationCalls { get; private set; }

        public ValueTask<Result<LoadedDateThread>> GetByServiceDateAsync(
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                organizationId == loaded.Thread.OrganizationId
                    && serviceDateId == loaded.Thread.ServiceDateId
                    ? Result.Success(loaded)
                    : Result.Failure<LoadedDateThread>(DomainError.NotFound("missing", "Missing.")));

        public ValueTask<Result<ThreadOrdinaryAuthorizationContext>>
            LockOrdinaryAuthorizationAsync(
                UserId userId,
                MembershipId membershipId,
                OrganizationId organizationId,
                ServiceDateId serviceDateId,
                ThreadAuthorizationRequirement requirement,
                CancellationToken cancellationToken = default)
        {
            OrdinaryAuthorizationCalls++;
            return ValueTask.FromResult(ordinaryAuthorization(requirement));
        }

        public ValueTask<Result<ThreadPrivilegedAuthorizationContext>>
            LockPrivilegedAuthorizationAsync(
                UserId userId,
                MembershipId membershipId,
                OrganizationId organizationId,
                CancellationToken cancellationToken = default)
        {
            PrivilegedAuthorizationCalls++;
            return ValueTask.FromResult(privilegedAuthorization());
        }

        public ValueTask<Result> SaveAsync(
            LoadedDateThread thread,
            ThreadPersistenceEffects effects,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed class FakeMembershipRepository(ActiveMembershipContext actor) : IMembershipRepository
    {
        public ActiveMembershipContext Actor { get; set; } = actor;
        public int ResolveCalls { get; private set; }

        public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return ValueTask.FromResult(
                userId == Actor.UserId
                    && membershipId == Actor.MembershipId
                    && organizationId == Actor.OrganizationId
                    ? Result.Success(Actor)
                    : Result.Failure<ActiveMembershipContext>(
                        DomainError.Unauthorized("unauthorized", "Unauthorized.")));
        }

        public ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextAsync(
            string invitationToken,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> AcceptInvitationAsync(
            string invitationToken,
            MembershipId membershipId,
            string displayName,
            DateTimeOffset acceptedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
            UserId userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> SaveGovernanceAsync(
            OrganizationAccountGovernance aggregate,
            MembershipId actorMembershipId,
            DateTimeOffset occurredAt,
            MembershipAdministrationPersistenceEffects effects,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> IssueInvitationAsync(
            IssueMembershipInvitationPersistenceRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyDictionary<MembershipId, string>>> GetThreadSenderDisplaysAsync(
            OrganizationId organizationId,
            IReadOnlyCollection<MembershipId> membershipIds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result.Success<IReadOnlyDictionary<MembershipId, string>>(
                    membershipIds.ToDictionary(id => id, _ => Actor.DisplayName)));
    }

    private sealed class RecordingPrivilegedAccessWriter(bool fails) : IPrivilegedAccessWriter
    {
        public int WriteCalls { get; private set; }

        public ValueTask WriteAsync(
            PrivilegedAccessEntry entry,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return fails
                ? ValueTask.FromException(new DependencyUnavailableException())
                : ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStepUpVerifier : IStepUpVerifier
    {
        public int ConsumeCalls { get; private set; }

        public ValueTask<Result<StepUpGrant>> IssueAsync(
            UserId userId,
            string credential,
            StepUpPurpose purpose,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<Result> ConsumeAsync(
            UserId userId,
            StepUpToken token,
            StepUpPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            ConsumeCalls++;
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed record CurrentActor(
        UserId UserId,
        MembershipId MembershipId,
        OrganizationId OrganizationId) : ICurrentActor;

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
