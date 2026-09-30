using System.Reflection;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Tests.Signups.Decisions;

public sealed class SignupDecisionServiceTests
{
    private static readonly string[] CommandPropertyNames =
        ["SignupId", "Reason", "IdempotencyKey"];
    private static readonly string[] ServiceMethodNames =
        ["ApproveAsync", "DeclineAsync", "WaitlistAsync"];
    private static readonly string[] ForbiddenCommandPropertyFragments =
    [
        "Actor", "Tenant", "Organization", "Membership", "Version", "Timestamp",
        "TargetState", "Aggregate", "HelpNeed", "ServiceDate", "Thread",
    ];

    [Fact]
    public void CommandsExposeOnlySignupIdReasonAndIdempotencyKeyAndVersionIsSeparate()
    {
        Type approve = DecisionTypes.Require("ApproveSignupCommand");
        Type decline = DecisionTypes.Require("DeclineSignupCommand");
        Type waitlist = DecisionTypes.Require("WaitlistSignupCommand");
        Type service = DecisionTypes.Require("SignupDecisionService");

        Assert.Equal(
            CommandPropertyNames,
            approve.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            CommandPropertyNames,
            decline.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            CommandPropertyNames,
            waitlist.GetProperties().Select(property => property.Name).ToArray());
        Assert.All(
            ServiceMethodNames,
            methodName =>
            {
                ParameterInfo[] parameters = service.GetMethod(methodName)!.GetParameters();
                Assert.Equal(3, parameters.Length);
                Assert.Equal(typeof(long), parameters[1].ParameterType);
                Assert.Equal("expectedSignupVersion", parameters[1].Name);
            });
        Assert.DoesNotContain(
            approve.GetProperties().Concat(decline.GetProperties()).Concat(waitlist.GetProperties()),
            property => ForbiddenCommandPropertyFragments.Any(
                fragment => property.Name.Contains(
                    fragment,
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task EachActionUsesCurrentActorForResolutionAndRepositoryArguments()
    {
        foreach (string action in DecisionHarness.Actions)
        {
            DecisionHarness harness = DecisionHarness.Create();

            Result<SignupSummary> result = await harness.InvokeAsync(action);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
            Assert.Equal(harness.Actor.UserId, harness.MembershipArguments![0]);
            Assert.Equal(harness.Actor.MembershipId, harness.MembershipArguments[1]);
            Assert.Equal(harness.Actor.OrganizationId, harness.MembershipArguments[2]);
            Assert.Equal(harness.Actor.OrganizationId, harness.ContextArguments![0]);
            Assert.Equal(harness.Actor.MembershipId, harness.ContextArguments[1]);
            Assert.Equal(harness.SignupId, harness.ContextArguments[2]);
        }
    }

    [Fact]
    public async Task EachNewActionCapturesOneClockValueAndPassesItToTheDomainTransition()
    {
        foreach (string action in DecisionHarness.Actions)
        {
            DecisionHarness harness = DecisionHarness.Create();

            Result<SignupSummary> result = await harness.InvokeAsync(action);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
            Assert.Equal(1, harness.Clock.ReadCount);
            Signup changed = Assert.Single(
                harness.Aggregate.Signups,
                signup => signup.Id == harness.SignupId);
            Assert.Equal(harness.Clock.Value, changed.LastTransitionAt);
        }
    }

    [Fact]
    public async Task OptionalReasonsNormalizeWhitespaceToNullAndDeclineRequiresTrimmedReason()
    {
        SignupId signupId = SignupId.New();
        DecisionHarness nullReason = DecisionHarness.Create(
            reason: null,
            signupId: signupId);
        DecisionHarness whitespace = DecisionHarness.Create(
            reason: " \t ",
            signupId: signupId);
        DecisionHarness decline = DecisionHarness.Create(reason: "  operational reason  ");

        Assert.True((await nullReason.InvokeAsync("Approve")).IsSuccess);
        Assert.True((await whitespace.InvokeAsync("Approve")).IsSuccess);
        Assert.Equal(
            nullReason.LastCreateRequest!.RequestFingerprint,
            whitespace.LastCreateRequest!.RequestFingerprint);
        Assert.True((await decline.InvokeAsync("Decline")).IsSuccess);
        Assert.Equal("operational reason", decline.SavedAuditReason);

        DecisionHarness blankDecline = DecisionHarness.Create(reason: "  ");
        Result<SignupSummary> invalid = await blankDecline.InvokeAsync("Decline");
        Assert.True(invalid.IsFailure);
        Assert.Equal(SignupApplicationErrorCodes.InvalidSignupRequest, invalid.Error.Code);
        Assert.Equal(0, blankDecline.IdempotencyCreateCalls);
    }

    [Fact]
    public async Task ReasonScalarAndUtf8BoundsReturnPayloadTooLargeWithoutWrites()
    {
        string scalarOverflow = new('x', ApplicationLimits.MaximumReasonUnicodeScalars + 1);
        string utf8Overflow = string.Concat(
            Enumerable.Repeat("😀", (ApplicationLimits.MaximumReasonUtf8Bytes / 4) + 1));

        foreach (string reason in new[] { scalarOverflow, utf8Overflow })
        {
            DecisionHarness harness = DecisionHarness.Create(reason: reason);

            Result<SignupSummary> result = await harness.InvokeAsync("Approve");

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.PayloadTooLarge, result.Error.Code);
            Assert.Equal(ErrorType.PayloadTooLarge, result.Error.Type);
            Assert.Equal(0, harness.RepositorySaveCalls);
            Assert.Equal(0, harness.IdempotencyCreateCalls);
            Assert.Equal(0, harness.UnitOfWork.CommitCount);
        }
    }

    [Fact]
    public async Task FingerprintIncludesActionTargetNormalizedReasonAndExpectedVersion()
    {
        SignupId signupId = SignupId.New();
        DecisionHarness baseline = DecisionHarness.Create(
            reason: "reason",
            signupId: signupId);
        DecisionHarness changedAction = DecisionHarness.Create(
            reason: "reason",
            signupId: signupId);
        DecisionHarness changedTarget = DecisionHarness.Create(
            reason: "reason",
            signupId: SignupId.New());
        DecisionHarness changedReason = DecisionHarness.Create(
            reason: "different",
            signupId: signupId);
        DecisionHarness changedVersion = DecisionHarness.Create(
            reason: "reason",
            signupId: signupId);

        await baseline.InvokeAsync("Approve");
        await changedAction.InvokeAsync("Waitlist");
        await changedTarget.InvokeAsync("Approve");
        await changedReason.InvokeAsync("Approve");
        await changedVersion.InvokeAsync("Approve", expectedVersion: baseline.ExpectedVersion + 1);

        (string Operation, RequestFingerprint Fingerprint)[] identities =
        [
            (baseline.LastCreateRequest!.Operation, baseline.LastCreateRequest.RequestFingerprint),
            (changedAction.LastCreateRequest!.Operation, changedAction.LastCreateRequest.RequestFingerprint),
            (changedTarget.LastCreateRequest!.Operation, changedTarget.LastCreateRequest.RequestFingerprint),
            (changedReason.LastCreateRequest!.Operation, changedReason.LastCreateRequest.RequestFingerprint),
            (changedVersion.LastCreateRequest!.Operation, changedVersion.LastCreateRequest.RequestFingerprint),
        ];
        Assert.Equal(identities.Length, identities.Distinct().Count());
        Assert.Equal(
            baseline.LastCreateRequest.RequestFingerprint,
            changedAction.LastCreateRequest.RequestFingerprint);
        Assert.Equal("signup.approve", baseline.LastCreateRequest.Operation);
        Assert.Equal("signup.waitlist", changedAction.LastCreateRequest.Operation);
    }

    [Fact]
    public async Task ExpectedVersionMismatchReturnsStaleVersionBeforeTransitionSaveOrCompletion()
    {
        DecisionHarness harness = DecisionHarness.Create();

        Result<SignupSummary> result = await harness.InvokeAsync(
            "Approve",
            expectedVersion: harness.ExpectedVersion + 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, result.Error.Code);
        Assert.Equal(ErrorType.PreconditionFailed, result.Error.Type);
        Assert.Equal(SignupStatus.Pending, harness.Target.Status);
        Assert.Equal(0, harness.RepositorySaveCalls);
        Assert.Equal(0, harness.IdempotencyCompleteCalls);
        Assert.Equal(0, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task InvalidTransitionCapacityChronologyAndExhaustionFailuresWriteNothing()
    {
        DecisionHarness[] cases =
        [
            DecisionHarness.Create(initialStatus: SignupStatus.Approved),
            DecisionHarness.Create(capacity: 0),
            DecisionHarness.Create(clock: DecisionHarness.SubmittedAt.AddTicks(-1)),
            DecisionHarness.Create(rootVersion: long.MaxValue),
        ];

        foreach (DecisionHarness harness in cases)
        {
            Result<SignupSummary> result = await harness.InvokeAsync("Approve");

            Assert.True(result.IsFailure);
            Assert.Contains(
                result.Error.Code,
                new[]
                {
                    ErrorCodes.InvalidTransition,
                    ErrorCodes.CapacityUnavailable,
                    SignupErrorCodes.SignupChronologyInvalid,
                    SignupErrorCodes.VersionExhausted,
                });
            Assert.Equal(0, harness.RepositorySaveCalls);
            Assert.Equal(0, harness.IdempotencyCompleteCalls);
            Assert.Equal(0, harness.UnitOfWork.CommitCount);
        }
    }

    [Fact]
    public async Task SuccessfulActionsCreateOneScopedAuditNotificationAndOutboxWithoutReasonLeakage()
    {
        foreach (string action in DecisionHarness.Actions)
        {
            string secretReason = "private decision reason";
            DecisionHarness harness = DecisionHarness.Create(reason: secretReason);

            Result<SignupSummary> result = await harness.InvokeAsync(action);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
            object write = harness.SavedWrite!;
            Assert.IsType(DecisionTypes.Require("SignupDecisionWrite"), write);
            object effects = write.GetType().GetProperty("Effects")!.GetValue(write)!;
            Array notifications = ToArray(effects, "Notifications");
            Array audits = ToArray(effects, "AuditEntries");
            Array outbox = ToArray(effects, "OutboxMessages");
            Assert.Single(notifications);
            Assert.Single(audits);
            Assert.Single(outbox);

            object notification = notifications.GetValue(0)!;
            Assert.Equal(harness.Target.PrimaryMembershipId, Get(notification, "RecipientMembershipId"));
            Assert.Equal(harness.SignupId.Value, Get(notification, "ResourceId"));
            Assert.Equal(harness.Actor.MembershipId, Get(audits.GetValue(0)!, "ActorMembershipId"));
            Assert.Equal(harness.IdempotencyKey.ToString(), Get(audits.GetValue(0)!, "CorrelationId"));

            Assert.DoesNotContain(
                secretReason,
                JsonSerializer.Serialize(result.Value),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                secretReason,
                JsonSerializer.Serialize(notification),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                secretReason,
                JsonSerializer.Serialize(outbox.GetValue(0)!),
                StringComparison.Ordinal);
            Assert.Contains(secretReason, JsonSerializer.Serialize(audits.GetValue(0)!));
        }
    }

    [Fact]
    public async Task CompletedRetryReauthorizesAndReturnsCurrentProjectionWithoutMutationOrEffects()
    {
        DecisionHarness harness = DecisionHarness.Create(
            idempotencyOutcome: IdempotencyCreateOutcome.ExistingCompleted);
        SignupSummary authoritative = harness.Summary with
        {
            Status = SignupStatus.Declined,
            Version = harness.Summary.Version + 1,
            SignupVersion = harness.Summary.SignupVersion + 1,
        };
        harness.Summary = authoritative;

        Result<SignupSummary> result = await harness.InvokeAsync("Approve");

        Assert.True(result.IsSuccess);
        Assert.Equal(authoritative, result.Value);
        Assert.NotNull(harness.MembershipArguments);
        Assert.NotNull(harness.ContextArguments);
        Assert.Equal(0, harness.RepositorySaveCalls);
        Assert.Equal(0, harness.IdempotencyCompleteCalls);
        Assert.Equal(1, harness.UnitOfWork.CommitCount);
    }

    [Fact]
    public async Task ProcessingFailedExpiredAndMismatchedReceiptsReturnStableConflicts()
    {
        (IdempotencyCreateOutcome Outcome, string Code)[] cases =
        [
            (IdempotencyCreateOutcome.ExistingProcessing, SignupApplicationErrorCodes.IdempotencyInProgress),
            (IdempotencyCreateOutcome.ExistingFailed, SignupApplicationErrorCodes.IdempotencyMismatch),
            (IdempotencyCreateOutcome.Expired, SignupApplicationErrorCodes.IdempotencyMismatch),
            (IdempotencyCreateOutcome.RequestMismatch, SignupApplicationErrorCodes.IdempotencyMismatch),
        ];

        foreach ((IdempotencyCreateOutcome outcome, string expectedCode) in cases)
        {
            DecisionHarness harness = DecisionHarness.Create(idempotencyOutcome: outcome);

            Result<SignupSummary> result = await harness.InvokeAsync("Approve");

            Assert.True(result.IsFailure);
            Assert.Equal(expectedCode, result.Error.Code);
            Assert.Equal(ErrorType.Conflict, result.Error.Type);
            Assert.Equal(0, harness.RepositorySaveCalls);
            Assert.Equal(0, harness.UnitOfWork.CommitCount);
        }
    }

    [Fact]
    public async Task SaveOrCompletionFailureRollsBackReceiptDecisionAndEffects()
    {
        DecisionHarness saveFailure = DecisionHarness.Create(
            saveResult: Result.Failure(
                DomainError.PreconditionFailed(
                    ErrorCodes.StaleVersion,
                    "simulated save failure")));
        Result<SignupSummary> failedSave = await saveFailure.InvokeAsync("Approve");
        Assert.True(failedSave.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, failedSave.Error.Code);
        Assert.Equal(0, saveFailure.IdempotencyCompleteCalls);
        Assert.Equal(0, saveFailure.UnitOfWork.CommitCount);

        DecisionHarness completionFailure = DecisionHarness.Create(
            completionOutcome: IdempotencyTransitionOutcome.ExpectedStatusMismatch);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => completionFailure.InvokeAsync("Approve"));
        Assert.Equal(1, completionFailure.RepositorySaveCalls);
        Assert.Equal(0, completionFailure.UnitOfWork.CommitCount);
    }

    private static Array ToArray(object owner, string propertyName)
    {
        object value = owner.GetType().GetProperty(propertyName)!.GetValue(owner)!;
        MethodInfo toArray = typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!
            .MakeGenericMethod(value.GetType().GetGenericArguments()[0]);
        return (Array)toArray.Invoke(null, [value])!;
    }

    private static object? Get(object owner, string propertyName) =>
        owner.GetType().GetProperty(propertyName)!.GetValue(owner);

    private sealed class DecisionHarness
    {
        public static readonly string[] Actions = ["Approve", "Decline", "Waitlist"];
        public static readonly DateTimeOffset SubmittedAt =
            new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        private readonly string? reason;
        private readonly IdempotencyCreateOutcome idempotencyOutcome;
        private readonly Result saveResult;
        private readonly IdempotencyTransitionOutcome completionOutcome;
        private readonly RecordingProxy membershipProxy;
        private readonly RecordingProxy signupProxy;
        private readonly RecordingProxy idempotencyProxy;
        private object? service;

        private DecisionHarness(
            string? reason,
            IdempotencyCreateOutcome idempotencyOutcome,
            Result saveResult,
            IdempotencyTransitionOutcome completionOutcome,
            SignupId signupId,
            int? capacity,
            SignupStatus initialStatus,
            DateTimeOffset clock,
            long rootVersion)
        {
            this.reason = reason;
            this.idempotencyOutcome = idempotencyOutcome;
            this.saveResult = saveResult;
            this.completionOutcome = completionOutcome;
            SignupId = signupId;
            IdempotencyKey = IdempotencyKey.New();
            Actor = new CurrentActor();
            Clock = new CountingClock(clock);
            UnitOfWork = new RecordingUnitOfWork();
            Aggregate = AggregateFactory.Create(
                Actor.OrganizationId,
                SignupId,
                Actor.MembershipId,
                capacity,
                initialStatus,
                rootVersion);
            Target = Aggregate.Signups.Single(signup => signup.Id == SignupId);
            ExpectedVersion = Aggregate.Version;
            Summary = AggregateFactory.ToSummary(Aggregate, Target);

            membershipProxy = RecordingProxy.Create<IMembershipRepository>(
                HandleMembership);
            signupProxy = RecordingProxy.Create<ISignupRepository>(
                HandleSignupRepository);
            idempotencyProxy = RecordingProxy.Create<IIdempotencyStore>(
                HandleIdempotency);
        }

        public CurrentActor Actor { get; }
        public CountingClock Clock { get; }
        public RecordingUnitOfWork UnitOfWork { get; }
        public HelpNeedSignups Aggregate { get; }
        public Signup Target { get; }
        public SignupId SignupId { get; }
        public IdempotencyKey IdempotencyKey { get; }
        public long ExpectedVersion { get; }
        public SignupSummary Summary { get; set; }
        public object?[]? MembershipArguments { get; private set; }
        public object?[]? ContextArguments { get; private set; }
        public IdempotencyCreateRequest? LastCreateRequest { get; private set; }
        public object? SavedWrite { get; private set; }
        public int RepositorySaveCalls { get; private set; }
        public int IdempotencyCreateCalls { get; private set; }
        public int IdempotencyCompleteCalls { get; private set; }
        public string? SavedAuditReason
        {
            get
            {
                if (SavedWrite is null)
                {
                    return null;
                }

                object effects = SavedWrite.GetType().GetProperty("Effects")!.GetValue(SavedWrite)!;
                return (string?)Get(ToArray(effects, "AuditEntries").GetValue(0)!, "Reason");
            }
        }

        public static DecisionHarness Create(
            string? reason = "reason",
            IdempotencyCreateOutcome idempotencyOutcome = IdempotencyCreateOutcome.Created,
            Result? saveResult = null,
            IdempotencyTransitionOutcome completionOutcome =
                IdempotencyTransitionOutcome.Completed,
            SignupId? signupId = null,
            int? capacity = 10,
            SignupStatus initialStatus = SignupStatus.Pending,
            DateTimeOffset? clock = null,
            long rootVersion = 1) =>
            new(
                reason,
                idempotencyOutcome,
                saveResult ?? Result.Success(),
                completionOutcome,
                signupId ?? SignupId.New(),
                capacity,
                initialStatus,
                clock ?? SubmittedAt.AddMinutes(1),
                rootVersion);

        public async Task<Result<SignupSummary>> InvokeAsync(
            string action,
            long? expectedVersion = null)
        {
            object serviceInstance = Service();
            Type commandType = DecisionTypes.Require($"{action}SignupCommand");
            object command = Activator.CreateInstance(
                commandType,
                SignupId,
                reason,
                IdempotencyKey)!;
            MethodInfo method = serviceInstance.GetType().GetMethod($"{action}Async")!;
            object valueTask = method.Invoke(
                serviceInstance,
                [command, expectedVersion ?? ExpectedVersion, CancellationToken.None])!;
            Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
            await task;
            return (Result<SignupSummary>)task.GetType().GetProperty("Result")!.GetValue(task)!;
        }

        private object Service()
        {
            if (service is not null)
            {
                return service;
            }

            Type type = DecisionTypes.Require("SignupDecisionService");
            service = Activator.CreateInstance(
                type,
                UnitOfWork,
                signupProxy.Interface,
                membershipProxy.Interface,
                idempotencyProxy.Interface,
                Actor,
                Clock)!;
            return service;
        }

        private object? HandleMembership(MethodInfo method, object?[]? arguments)
        {
            if (method.Name != "ResolveActiveActorAsync")
            {
                throw new InvalidOperationException($"Unexpected membership call: {method.Name}.");
            }

            MembershipArguments = arguments;
            ActiveMembershipContext actor = new(
                Actor.UserId,
                Actor.MembershipId,
                Actor.OrganizationId,
                "Manager",
                true,
                [OrganizationRole.FoodIncharge],
                "Organization",
                "America/Los_Angeles");
            return new ValueTask<Result<ActiveMembershipContext>>(Result.Success(actor));
        }

        private object? HandleSignupRepository(MethodInfo method, object?[]? arguments)
        {
            if (method.Name == "GetDecisionContextAsync")
            {
                ContextArguments = arguments;
                Type contextType = DecisionTypes.Require("SignupDecisionContext");
                object context = Activator.CreateInstance(contextType, Aggregate, Summary)!;
                object result = ReflectionResult.Success(contextType, context);
                return ReflectionResult.ValueTask(method.ReturnType, result);
            }

            if (method.Name == "SaveDecisionAsync")
            {
                RepositorySaveCalls++;
                SavedWrite = arguments![1];
                return new ValueTask<Result>(saveResult);
            }

            throw new InvalidOperationException($"Unexpected signup repository call: {method.Name}.");
        }

        private object? HandleIdempotency(MethodInfo method, object?[]? arguments)
        {
            if (method.Name == "TryCreateProcessingAsync")
            {
                IdempotencyCreateCalls++;
                LastCreateRequest = (IdempotencyCreateRequest)arguments![0]!;
                IdempotencyStatus status = idempotencyOutcome switch
                {
                    IdempotencyCreateOutcome.ExistingCompleted => IdempotencyStatus.Completed,
                    IdempotencyCreateOutcome.ExistingFailed => IdempotencyStatus.Failed,
                    _ => IdempotencyStatus.Processing,
                };
                IdempotencyReceipt receipt = Receipt(
                    LastCreateRequest,
                    status,
                    status == IdempotencyStatus.Completed ? SignupId.ToString() : null);
                return new ValueTask<IdempotencyCreateResult>(
                    new IdempotencyCreateResult(idempotencyOutcome, receipt));
            }

            if (method.Name == "TryCompleteAsync")
            {
                IdempotencyCompleteCalls++;
                IdempotencyRequest request = (IdempotencyRequest)arguments![0]!;
                IdempotencyStatus status =
                    completionOutcome == IdempotencyTransitionOutcome.Completed
                        ? IdempotencyStatus.Completed
                        : IdempotencyStatus.Failed;
                IdempotencyReceipt receipt = new(
                    request.OrganizationId,
                    request.MembershipId,
                    request.Key,
                    request.Operation,
                    request.RequestFingerprint,
                    status,
                    status == IdempotencyStatus.Completed ? arguments[1]!.ToString() : null,
                    SubmittedAt,
                    SubmittedAt.AddHours(24));
                return new ValueTask<IdempotencyTransitionResult>(
                    new IdempotencyTransitionResult(completionOutcome, receipt));
            }

            throw new InvalidOperationException($"Unexpected idempotency call: {method.Name}.");
        }

        private static IdempotencyReceipt Receipt(
            IdempotencyCreateRequest request,
            IdempotencyStatus status,
            string? resultReference) =>
            new(
                request.OrganizationId,
                request.MembershipId,
                request.Key,
                request.Operation,
                request.RequestFingerprint,
                status,
                resultReference,
                request.CreatedAt,
                request.ExpiresAt);
    }

    private static class AggregateFactory
    {
        public static HelpNeedSignups Create(
            OrganizationId organizationId,
            SignupId signupId,
            MembershipId primaryMembershipId,
            int? capacity,
            SignupStatus status,
            long rootVersion)
        {
            ServiceDateId dateId = ServiceDateId.New();
            HelpNeedId needId = HelpNeedId.New();
            int? persistedCapacity = capacity == 0 ? 1 : capacity;
            HelpNeed need = HelpNeed.Rehydrate(
                needId,
                dateId,
                HelpCategory.FoodPreparation,
                "Prepare food.",
                persistedCapacity,
                HelpNeedStatus.Open,
                0).Value;
            ServiceDate date = ServiceDate.Rehydrate(
                dateId,
                organizationId,
                "Service date",
                "Prepare.",
                DecisionHarness.SubmittedAt.AddDays(1),
                DecisionHarness.SubmittedAt.AddDays(1).AddHours(4),
                DecisionHarness.SubmittedAt.AddHours(23),
                MembershipId.New(),
                ServiceDateStatus.Open,
                2,
                [need]).Value;
            HelpNeedSignups aggregate = Rehydrate(date, need, 0, 0, []);
            Membership primary = ActiveMembership(
                primaryMembershipId,
                organizationId,
                "Primary");
            Assert.True(aggregate.Submit(
                signupId,
                primary,
                SignupKind.Individual,
                [],
                0,
                DecisionHarness.SubmittedAt).IsSuccess);

            if (capacity == 0)
            {
                SignupId approvedSignupId = SignupId.New();
                Assert.True(aggregate.Submit(
                    approvedSignupId,
                    ActiveMembership(
                        MembershipId.New(),
                        organizationId,
                        "Approved primary"),
                    SignupKind.Individual,
                    [],
                    0,
                    DecisionHarness.SubmittedAt).IsSuccess);
                Assert.True(aggregate.Approve(
                    approvedSignupId,
                    DecisionHarness.SubmittedAt.AddSeconds(1)).IsSuccess);
            }

            if (status == SignupStatus.Approved)
            {
                Assert.True(aggregate.Approve(
                    signupId,
                    DecisionHarness.SubmittedAt.AddSeconds(1)).IsSuccess);
            }
            else if (status == SignupStatus.Waitlisted)
            {
                Assert.True(aggregate.Waitlist(
                    signupId,
                    DecisionHarness.SubmittedAt.AddSeconds(1)).IsSuccess);
            }
            else if (status == SignupStatus.Declined)
            {
                Assert.True(aggregate.Decline(
                    signupId,
                    DecisionHarness.SubmittedAt.AddSeconds(1)).IsSuccess);
            }

            long highWater = aggregate.WaitlistOrderHighWater;
            return Rehydrate(date, need, rootVersion, highWater, aggregate.Signups);
        }

        public static SignupSummary ToSummary(HelpNeedSignups aggregate, Signup signup) =>
            new(
                signup.Id,
                signup.ServiceDateId,
                signup.HelpNeedId,
                HelpCategory.FoodPreparation,
                new SignupParticipantSummary(signup.PrimaryMembershipId, "Primary"),
                signup.Kind,
                null,
                [],
                signup.UnnamedParticipantCount,
                signup.TotalParticipantCount,
                signup.Status,
                signup.SubmittedAt,
                signup.LastTransitionAt,
                signup.WaitlistOrder,
                signup.Version,
                aggregate.Version);

        private static HelpNeedSignups Rehydrate(
            ServiceDate date,
            HelpNeed need,
            long rootVersion,
            long highWater,
            IReadOnlyCollection<Signup> signups)
        {
            Type factory = typeof(HelpNeedSignups).GetNestedType(
                "PersistenceFactory",
                BindingFlags.NonPublic)!;
            MethodInfo method = factory.GetMethod(
                "Rehydrate",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            object result = method.Invoke(null, [date, need, rootVersion, highWater, signups])!;
            return (HelpNeedSignups)result.GetType().GetProperty("Value")!.GetValue(result)!;
        }

        private static Membership ActiveMembership(
            MembershipId id,
            OrganizationId organizationId,
            string displayName)
        {
            Membership membership = Membership.Invite(
                id,
                organizationId,
                UserId.New(),
                displayName,
                true).Value;
            Assert.True(membership.Activate(DateTimeOffset.UnixEpoch).IsSuccess);
            return membership;
        }
    }

    private static class DecisionTypes
    {
        private const string Namespace =
            "HusayniaTabruk.Application.Signups.Decisions";

        public static Type Require(string name)
        {
            Type assemblyMarker = typeof(IUnitOfWork);
            Type? type = assemblyMarker.Assembly.GetType($"{Namespace}.{name}")
                ?? assemblyMarker.Assembly.GetType(
                    $"HusayniaTabruk.Application.Abstractions.Persistence.{name}");
            Assert.NotNull(type);
            return type;
        }
    }

    private static class ReflectionResult
    {
        public static object Success(Type type, object value)
        {
            MethodInfo method = typeof(Result).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(candidate =>
                    candidate.Name == nameof(Result.Success)
                    && candidate.IsGenericMethodDefinition
                    && candidate.GetParameters().Length == 1);
            return method.MakeGenericMethod(type).Invoke(null, [value])!;
        }

        public static object ValueTask(Type valueTaskType, object result) =>
            Activator.CreateInstance(valueTaskType, result)!;
    }

    public class RecordingProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> handler = null!;

        public object Interface { get; private set; } = null!;

        public static RecordingProxy Create<T>(
            Func<MethodInfo, object?[]?, object?> handler)
            where T : class
        {
            T value = DispatchProxy.Create<T, RecordingProxy>();
            RecordingProxy proxy = (RecordingProxy)(object)value;
            proxy.handler = handler;
            proxy.Interface = value;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            handler(
                targetMethod ?? throw new InvalidOperationException("Missing proxy target method."),
                args);
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

    private sealed class CurrentActor : ICurrentActor
    {
        public UserId UserId { get; } = UserId.New();
        public MembershipId MembershipId { get; } = MembershipId.New();
        public OrganizationId OrganizationId { get; } = OrganizationId.New();
    }

    private sealed class CountingClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset Value { get; } = value;
        public int ReadCount { get; private set; }
        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCount++;
                return Value;
            }
        }
    }
}
