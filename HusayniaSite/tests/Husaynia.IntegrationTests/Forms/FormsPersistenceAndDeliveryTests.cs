using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Retention;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Forms;
using Husaynia.Domain.Identity;
using Husaynia.Domain.Operations.Jobs;
using Husaynia.Infrastructure.Forms;
using Husaynia.Infrastructure.Operations.Jobs;
using Husaynia.Infrastructure.Operations.Retention;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.IntegrationTests.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Husaynia.IntegrationTests.Forms;

public sealed class FormsPersistenceAndDeliveryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentDuplicateCreatesExactlyOneSubmissionValueJobAndAudit()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ConcurrentDuplicateCreatesExactlyOneSubmissionValueJobAndAudit));
        var definition = await SeedDefinitionAsync(database);
        var request = CreateSubmission(definition, Enumerable.Repeat((byte)7, 32).ToArray());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = SubmitAfterSignalAsync(database, request, start.Task);
        var second = SubmitAfterSignalAsync(database, request, start.Task);
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(
            results,
            result => Assert.True(
                result.IsSuccess,
                result.IsFailure ? result.Error.Code : string.Empty));
        Assert.Equal(results[0].Success.SubmissionId, results[1].Success.SubmissionId);
        Assert.Equal(1, await database.CountRowsAsync("FormSubmissions"));
        Assert.Equal(1, await database.CountRowsAsync("FormSubmissionValues"));
        Assert.Equal(1, await database.CountRowsAsync("OperationsJobInstances"));
        await using var verification = database.CreateContext();
        Assert.Single(
            await verification.Set<AuditEvent>()
                .AsNoTracking()
                .Where(audit => audit.Action == "forms.submission.accept")
                .ToArrayAsync());
        var job = await verification.Set<JobInstance>().AsNoTracking().SingleAsync();
        Assert.DoesNotContain("person@example.test", job.PayloadJson, StringComparison.Ordinal);
        Assert.True(FormDeliveryJobPayload.TryParse(job.PayloadJson, out var payload));
        Assert.Equal(results[0].Success.SubmissionId, payload.SubmissionId);
    }

    [Fact]
    public async Task SameFingerprintWithDifferentHashReturnsConflictWithoutPartialState()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(SameFingerprintWithDifferentHashReturnsConflictWithoutPartialState));
        var definition = await SeedDefinitionAsync(database);
        var fingerprint = Enumerable.Repeat((byte)9, 32).ToArray();

        var first = await SubmitAsync(database, CreateSubmission(definition, fingerprint));
        var conflicting = CreateSubmission(definition, fingerprint) with
        {
            CanonicalPayloadHash = Enumerable.Repeat((byte)5, 32).ToArray(),
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["email"] = "other@example.test",
            },
        };
        var second = await SubmitAsync(database, conflicting);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsFailure);
        Assert.Equal("duplicate_conflict", second.Error.Code);
        Assert.Equal(1, await database.CountRowsAsync("FormSubmissions"));
        Assert.Equal(1, await database.CountRowsAsync("FormSubmissionValues"));
        Assert.Equal(1, await database.CountRowsAsync("OperationsJobInstances"));
    }

    [Fact]
    public async Task SqlRateLimitIsAtomicAcrossContextsAndFailsAtNPlusOne()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(SqlRateLimitIsAtomicAcrossContextsAndFailsAtNPlusOne));
        var options = CreateOptions(permitLimit: 4);
        var time = new FixedTimeProvider(Now);
        var fingerprint = Enumerable.Repeat((byte)3, 32).ToArray();
        var attempts = Enumerable.Range(0, 5)
            .Select(_ => AttemptRateLimitAsync(database, options, time, fingerprint))
            .ToArray();

        var decisions = await Task.WhenAll(attempts);

        Assert.Equal(4, decisions.Count(decision => decision.IsAllowed));
        Assert.Single(decisions, decision => !decision.IsAllowed);
        Assert.Equal(1, await database.CountRowsAsync("FormRateLimits"));
    }

    [Fact]
    public async Task PickupDeliveryReloadsPersistedValuesOutsideJobPayloadAndRecordsAttempt()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(PickupDeliveryReloadsPersistedValuesOutsideJobPayloadAndRecordsAttempt));
        var definition = await SeedDefinitionAsync(database);
        var receipt = await SubmitAsync(
            database,
            CreateSubmission(definition, Enumerable.Repeat((byte)4, 32).ToArray()));
        Assert.True(receipt.IsSuccess);
        var pickupDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "forms-pickup",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var context = database.CreateContext();
            var options = CreateOptions(
                deliveryMode: FormDeliveryMode.Pickup,
                pickupDirectory: pickupDirectory);
            var store = CreateStore(context, options);
            var handler = new FormDeliveryJobHandler(
                store,
                new PickupFormMessageSender(options, new FixedTimeProvider(Now)));
            var job = await context.Set<JobInstance>().AsNoTracking().SingleAsync();
            var result = await handler.ExecuteAsync(
                job.PayloadJson,
                new JobExecutionContext(job.Id, job.IdempotencyKey, job.CorrelationId, 1),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            var pickupFile = Assert.Single(Directory.GetFiles(pickupDirectory, "*.json"));
            var pickup = await File.ReadAllTextAsync(pickupFile);
            Assert.Contains("person@example.test", pickup, StringComparison.Ordinal);
            var attempt = await context.Set<FormDeliveryAttempt>().AsNoTracking().SingleAsync();
            Assert.Equal(FormDeliveryAttemptOutcome.Succeeded, attempt.Outcome);
            Assert.NotNull(attempt.ProviderReceiptHash);
        }
        finally
        {
            if (Directory.Exists(pickupDirectory))
            {
                Directory.Delete(pickupDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DisabledSenderFailureCanBeRetriedThenDeadLetteredWithoutLosingSubmission()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(DisabledSenderFailureCanBeRetriedThenDeadLetteredWithoutLosingSubmission));
        var definition = await SeedDefinitionAsync(database);
        var receipt = await SubmitAsync(
            database,
            CreateSubmission(definition, Enumerable.Repeat((byte)6, 32).ToArray()));
        Assert.True(receipt.IsSuccess);

        await using var context = database.CreateContext();
        var jobStore = new EfDurableJobStore(context, new FixedTimeProvider(Now));
        var lease = (await jobStore.TryAcquireNextAsync(
            new WorkerIdentity("forms-test-worker"),
            TimeSpan.FromMinutes(1),
            Now,
            CancellationToken.None)).Success.Job!;
        var handler = new FormDeliveryJobHandler(
            CreateStore(context, CreateOptions()),
            new DisabledFormMessageSender());
        var result = await handler.ExecuteAsync(
            lease.PayloadJson,
            new JobExecutionContext(
                lease.JobInstanceId,
                lease.IdempotencyKey,
                lease.CorrelationId,
                lease.AttemptNumber),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("sender_disabled", result.ErrorCode);

        Assert.True((await jobStore.FailAsync(
            lease.JobInstanceId,
            lease.LeaseToken,
            result.ErrorCode!,
            Now.AddMinutes(1),
            Now,
            CancellationToken.None)).IsSuccess);
        var retry = (await jobStore.TryAcquireNextAsync(
            new WorkerIdentity("forms-test-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(1),
            CancellationToken.None)).Success.Job!;
        var retryResult = await handler.ExecuteAsync(
            retry.PayloadJson,
            new JobExecutionContext(
                retry.JobInstanceId,
                retry.IdempotencyKey,
                retry.CorrelationId,
                retry.AttemptNumber),
            CancellationToken.None);
        Assert.False(retryResult.IsSuccess);
        Assert.True((await jobStore.FailAsync(
            retry.JobInstanceId,
            retry.LeaseToken,
            retryResult.ErrorCode!,
            null,
            Now.AddMinutes(1),
            CancellationToken.None)).IsSuccess);

        var state = await jobStore.GetStateAsync(lease.JobInstanceId, CancellationToken.None);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.Equal(1, await database.CountRowsAsync("FormSubmissions"));
        Assert.Equal(2, await database.CountRowsAsync("FormDeliveryAttempts"));
    }

    [Fact]
    public async Task LegalHoldFencesRetentionAndReleaseAllowsValueAnonymization()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(LegalHoldFencesRetentionAndReleaseAllowsValueAnonymization));
        var definition = await SeedDefinitionAsync(database);
        var acceptedAt = Now.AddDays(-100);
        var request = CreateSubmission(
            definition,
            Enumerable.Repeat((byte)8, 32).ToArray()) with
        {
            AcceptedAtUtc = acceptedAt,
        };
        var submitted = await SubmitAsync(database, request);
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var store = CreateStore(context, CreateOptions());
        var audit = new IdentityAuditDescriptor(
            "site-admin",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            FormAdministrationService.RetentionChangeAction,
            "FormSubmission",
            submitted.Success.SubmissionId.ToString("N"),
            "forms-retention");
        var stateRowVersion = await ReadSubmissionRowVersionAsync(
            context,
            submitted.Success.SubmissionId);
        var held = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.PlaceLegalHold,
                stateRowVersion),
            audit,
            CancellationToken.None);
        var rejected = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.Anonymize,
                held.Success.StateRowVersion),
            audit with { CorrelationId = "forms-retention-held" },
            CancellationToken.None);
        var released = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.ReleaseLegalHold,
                held.Success.StateRowVersion),
            audit with { CorrelationId = "forms-retention-release" },
            CancellationToken.None);
        var eligible = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.MarkEligible,
                released.Success.StateRowVersion),
            audit with { CorrelationId = "forms-retention-eligible" },
            CancellationToken.None);
        var anonymized = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.Anonymize,
                eligible.Success.StateRowVersion),
            audit with { CorrelationId = "forms-retention-apply" },
            CancellationToken.None);

        Assert.True(held.IsSuccess);
        Assert.True(rejected.IsFailure);
        Assert.Equal("legal_hold", rejected.Error.Code);
        Assert.True(released.IsSuccess);
        Assert.True(eligible.IsSuccess);
        Assert.True(anonymized.IsSuccess);
        Assert.Equal(FormRetentionStatus.Anonymized, anonymized.Success.Status);
        Assert.Equal(
            string.Empty,
            await context.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == submitted.Success.SubmissionId)
                .Select(value => value.Value)
                .SingleAsync());
    }

    [Fact]
    public async Task DeliveryCancellationRecordsSanitizedCancelledAttempt()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(DeliveryCancellationRecordsSanitizedCancelledAttempt));
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(definition, Enumerable.Repeat((byte)10, 32).ToArray()));
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var job = await context.Set<JobInstance>().AsNoTracking().SingleAsync();
        var handler = new FormDeliveryJobHandler(
            CreateStore(context, CreateOptions()),
            new CancellingSender());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.ExecuteAsync(
                job.PayloadJson,
                new JobExecutionContext(job.Id, job.IdempotencyKey, job.CorrelationId, 1),
                CancellationToken.None));

        var attempt = await context.Set<FormDeliveryAttempt>().AsNoTracking().SingleAsync();
        Assert.Equal(FormDeliveryAttemptOutcome.Cancelled, attempt.Outcome);
        Assert.Equal("cancelled", attempt.ErrorCode);
    }

    [Fact]
    public async Task MissingRateLimitSchemaFailsClosed()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(MissingRateLimitSchemaFailsClosed));
        await using (var context = database.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE [dbo].[FormRateLimits];");
        }

        await using var limiterContext = database.CreateContext();
        var limiter = new SqlFormsRateLimiter(
            limiterContext,
            CreateOptions(),
            new FixedTimeProvider(Now),
            NullLogger<SqlFormsRateLimiter>.Instance);

        await Assert.ThrowsAsync<FormsRateLimitDependencyException>(
            () => limiter.AttemptAsync(
                "test-contact-v1",
                new byte[32],
                CancellationToken.None));
        Assert.Equal(0, await database.CountRowsAsync("FormSubmissions"));
    }

    [Fact]
    public async Task WrongDeliveryJobIdentityCannotLoadValuesSendOrCreateAttempt()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(WrongDeliveryJobIdentityCannotLoadValuesSendOrCreateAttempt));
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(definition, Enumerable.Repeat((byte)11, 32).ToArray()));
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var job = await context.Set<JobInstance>().AsNoTracking().SingleAsync();
        var sender = new RecordingSender();
        var handler = new FormDeliveryJobHandler(
            CreateStore(context, CreateOptions()),
            sender);
        var result = await handler.ExecuteAsync(
            job.PayloadJson,
            new JobExecutionContext(
                Guid.NewGuid(),
                job.IdempotencyKey,
                job.CorrelationId,
                1),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("delivery_job_mismatch", result.ErrorCode);
        Assert.Equal(0, sender.CallCount);
        Assert.Equal(0, await context.Set<FormDeliveryAttempt>().CountAsync());
    }

    [Fact]
    public async Task DefinitionAndRetentionMutationsRejectStaleRowVersions()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(DefinitionAndRetentionMutationsRejectStaleRowVersions));
        var definition = await SeedDefinitionAsync(database);
        await using var context = database.CreateContext();
        var store = CreateStore(context, CreateOptions());
        var actorRoles = new HashSet<string>(
            [RoleNames.SiteAdministrator],
            StringComparer.Ordinal);
        var publishCommand = new PublishFormDefinitionCommand(
            definition.FormKey,
            "Synthetic contact v2",
            "test-destination",
            "test-template",
            "test-consent-v2",
            [
                new FormFieldDraft(
                    "email",
                    "Email",
                    FormFieldKind.Email,
                    true,
                    3,
                    320,
                    null,
                    null,
                    FormPatternKind.Email,
                    [],
                    1,
                    FormPrivacyClass.Contact),
            ],
            definition.StateRowVersion);
        var published = await store.PublishAsync(
            publishCommand,
            new IdentityAuditDescriptor(
                "site-admin",
                actorRoles,
                FormAdministrationService.DefinitionPublishAction,
                "FormDefinition",
                definition.DefinitionId.ToString("N"),
                "forms-definition-cas-success"),
            CancellationToken.None);
        var stalePublish = await store.PublishAsync(
            publishCommand,
            new IdentityAuditDescriptor(
                "site-admin",
                actorRoles,
                FormAdministrationService.DefinitionPublishAction,
                "FormDefinition",
                definition.DefinitionId.ToString("N"),
                "forms-definition-cas-stale"),
            CancellationToken.None);

        Assert.True(published.IsSuccess);
        Assert.True(stalePublish.IsFailure);
        Assert.Equal("definition_conflict", stalePublish.Error.Code);
        Assert.Equal(2, await context.Set<FormDefinitionVersion>().CountAsync());

        var oldAcceptedAt = Now.AddDays(-100);
        var activeDefinition = await store.GetActiveAsync(
            definition.FormKey,
            CancellationToken.None);
        Assert.True(activeDefinition.IsSuccess);
        var submitted = await store.SubmitAsync(
            CreateSubmission(
                activeDefinition.Success,
                Enumerable.Repeat((byte)12, 32).ToArray()) with
            {
                AcceptedAtUtc = oldAcceptedAt,
            },
            CancellationToken.None);
        Assert.True(submitted.IsSuccess);
        var initialRowVersion = await ReadSubmissionRowVersionAsync(
            context,
            submitted.Success.SubmissionId);
        var eligible = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.MarkEligible,
                initialRowVersion),
            new IdentityAuditDescriptor(
                "site-admin",
                actorRoles,
                FormAdministrationService.RetentionChangeAction,
                "FormSubmission",
                submitted.Success.SubmissionId.ToString("N"),
                "forms-retention-cas-success"),
            CancellationToken.None);
        var staleRetention = await store.ChangeRetentionAsync(
            new FormRetentionCommand(
                submitted.Success.SubmissionId,
                FormRetentionAction.PlaceLegalHold,
                initialRowVersion),
            new IdentityAuditDescriptor(
                "site-admin",
                actorRoles,
                FormAdministrationService.RetentionChangeAction,
                "FormSubmission",
                submitted.Success.SubmissionId.ToString("N"),
                "forms-retention-cas-stale"),
            CancellationToken.None);

        Assert.True(eligible.IsSuccess);
        Assert.True(staleRetention.IsFailure);
        Assert.Equal("retention_conflict", staleRetention.Error.Code);
        Assert.False(
            await context.Set<FormSubmission>()
                .Where(submission => submission.Id == submitted.Success.SubmissionId)
                .Select(submission => submission.HasLegalHold)
                .SingleAsync());
    }

    [Fact]
    public async Task RequeueAuditFailureLeavesDeadLetteredThenAuditedRetryStoresOnlyReasonHash()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(RequeueAuditFailureLeavesDeadLetteredThenAuditedRetryStoresOnlyReasonHash));
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(definition, Enumerable.Repeat((byte)13, 32).ToArray()));
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var time = new FixedTimeProvider(Now);
        var jobStore = new EfDurableJobStore(context, time);
        var lease = (await jobStore.TryAcquireNextAsync(
            new WorkerIdentity("forms-requeue-test"),
            TimeSpan.FromMinutes(1),
            Now,
            CancellationToken.None)).Success.Job!;
        Assert.True((await jobStore.FailAsync(
            lease.JobInstanceId,
            lease.LeaseToken,
            "synthetic_failure",
            nextRunAtUtc: null,
            Now,
            CancellationToken.None)).IsSuccess);
        var failingFinalizer = new ThrowingAuditFinalizer();
        var store = new EfFormsStore(
            context,
            CreateOptions(),
            time,
            new HostileSensitiveDataRedactor(),
            jobStore,
            new NoopAuditWriter(),
            failingFinalizer);
        const string rawReason = "private admin reason person@example.test";

        var result = await store.RetryDeliveryAsync(
            submitted.Success.SubmissionId,
            rawReason,
            new IdentityAuditDescriptor(
                "site-admin",
                new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
                FormAdministrationService.DeliveryRetryAction,
                "FormSubmission",
                submitted.Success.SubmissionId.ToString("N"),
                "forms-requeue-audit-failure"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("forms_audit_unavailable", result.Error.Code);
        Assert.Equal(1, failingFinalizer.CallCount);
        var state = await jobStore.GetStateAsync(lease.JobInstanceId, CancellationToken.None);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.False(state.Success.Snapshot.CancellationRequested);
        Assert.Equal(0, await context.Set<JobOperationalAudit>().CountAsync());

        var successfulStore = new EfFormsStore(
            context,
            CreateOptions(),
            time,
            new HostileSensitiveDataRedactor(),
            jobStore,
            new NoopAuditWriter(),
            new NoopAuditFinalizer());
        var successful = await successfulStore.RetryDeliveryAsync(
            submitted.Success.SubmissionId,
            rawReason,
            new IdentityAuditDescriptor(
                "site-admin",
                new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
                FormAdministrationService.DeliveryRetryAction,
                "FormSubmission",
                submitted.Success.SubmissionId.ToString("N"),
                "forms-requeue-success"),
            CancellationToken.None);

        Assert.True(successful.IsSuccess);
        state = await jobStore.GetStateAsync(lease.JobInstanceId, CancellationToken.None);
        Assert.Equal("RetryScheduled", state.Success.Snapshot!.State);
        var operationalAudit = await context.Set<JobOperationalAudit>()
            .AsNoTracking()
            .SingleAsync(audit => audit.JobInstanceId == lease.JobInstanceId);
        Assert.StartsWith("sha256:", operationalAudit.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(rawReason, operationalAudit.Reason, StringComparison.Ordinal);
        Assert.Equal(0, await context.Set<FormDeliveryAttempt>().CountAsync());
        Assert.Equal(1, await context.Set<FormSubmission>().CountAsync());
    }

    [Fact]
    public async Task RequeueRemainsDeadLetteredUntilFormsAuditCompletes()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(RequeueRemainsDeadLetteredUntilFormsAuditCompletes));
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(definition, Enumerable.Repeat((byte)20, 32).ToArray()));
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var time = new FixedTimeProvider(Now);
        var jobStore = new EfDurableJobStore(context, time);
        var lease = (await jobStore.TryAcquireNextAsync(
            new WorkerIdentity("forms-requeue-order"),
            TimeSpan.FromMinutes(1),
            Now,
            CancellationToken.None)).Success.Job!;
        Assert.True((await jobStore.FailAsync(
            lease.JobInstanceId,
            lease.LeaseToken,
            "synthetic_failure",
            nextRunAtUtc: null,
            Now,
            CancellationToken.None)).IsSuccess);
        var finalizer = new BlockingAuditFinalizer();
        var store = new EfFormsStore(
            context,
            CreateOptions(),
            time,
            new HostileSensitiveDataRedactor(),
            jobStore,
            new NoopAuditWriter(),
            finalizer);

        var retry = store.RetryDeliveryAsync(
            submitted.Success.SubmissionId,
            "synthetic retry reason",
            new IdentityAuditDescriptor(
                "site-admin",
                new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
                FormAdministrationService.DeliveryRetryAction,
                "FormSubmission",
                submitted.Success.SubmissionId.ToString("N"),
                "forms-requeue-order"),
            CancellationToken.None);
        await finalizer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var observation = database.CreateContext())
        {
            var state = await new EfDurableJobStore(observation)
                .GetStateAsync(lease.JobInstanceId, CancellationToken.None);
            Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
            Assert.Equal(0, await observation.Set<JobOperationalAudit>().CountAsync());
        }

        finalizer.Release.TrySetResult();
        var result = await retry;

        Assert.True(result.IsSuccess);
        var completedState = await jobStore.GetStateAsync(
            lease.JobInstanceId,
            CancellationToken.None);
        Assert.Equal("RetryScheduled", completedState.Success.Snapshot!.State);
        Assert.Equal(1, await context.Set<JobOperationalAudit>().CountAsync());
    }

    [Fact]
    public async Task RetentionSinkRejectsDetachedExpiredHeldWrongStatusAndFuturePermits()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(RetentionSinkRejectsDetachedExpiredHeldWrongStatusAndFuturePermits));
        var definition = await SeedDefinitionAsync(database);
        var futureSubmission = await SubmitAsync(
            database,
            CreateSubmission(
                definition,
                Enumerable.Repeat((byte)14, 32).ToArray()) with
            {
                AcceptedAtUtc = Now,
            });
        var dueSubmission = await SubmitAsync(
            database,
            CreateSubmission(
                definition,
                Enumerable.Repeat((byte)15, 32).ToArray()) with
            {
                AcceptedAtUtc = Now.AddDays(-100),
            });
        Assert.True(futureSubmission.IsSuccess);
        Assert.True(dueSubmission.IsSuccess);

        await using var context = database.CreateContext();
        var clock = new SettableTimeProvider(Now);
        var retentionStore = new EfRetentionStore(context);
        var target = new FormSubmissionRetentionTarget(context, clock);
        var detached = await target.ApplyAsync(
            new RetentionApplyPermit(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new RetentionCandidate(
                    FormSubmissionRetentionTarget.TargetName,
                    dueSubmission.Success.SubmissionId.ToString("N"),
                    Now.AddDays(-10)),
                "detached"),
            CancellationToken.None);
        Assert.True(detached.IsFailure);

        var futureEntity = await context.Set<FormSubmission>()
            .SingleAsync(submission => submission.Id == futureSubmission.Success.SubmissionId);
        futureEntity.MarkRetentionEligible();
        var dueEntity = await context.Set<FormSubmission>()
            .SingleAsync(submission => submission.Id == dueSubmission.Success.SubmissionId);
        dueEntity.MarkRetentionEligible();
        await context.SaveChangesAsync();

        var futurePermit = await CreateRetentionPermitAsync(
            retentionStore,
            futureEntity,
            Now,
            "forms-future");
        var future = await target.ApplyAsync(futurePermit, CancellationToken.None);
        Assert.True(future.IsFailure);
        Assert.Equal("forms_retention_not_due", future.Error);

        dueEntity = await context.Set<FormSubmission>()
            .SingleAsync(submission => submission.Id == dueSubmission.Success.SubmissionId);
        var heldPermit = await CreateRetentionPermitAsync(
            retentionStore,
            dueEntity,
            Now,
            "forms-held");
        var holdId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO [dbo].[OperationsRetentionHolds]
                 ([Id], [IdempotencyKey], [Target], [SubjectId], [Kind], [Actor], [Reason],
                  [StartsAtUtc], [ExpiresAtUtc], [ReleasedAtUtc], [ReleasedBy], [ReleaseReason])
             VALUES
                 ({holdId}, {"forms-race-hold"}, {FormSubmissionRetentionTarget.TargetName},
                  {dueEntity.Id.ToString("N")}, {"Legal"}, {"site-admin"}, {"synthetic hold"},
                  {Now}, {(DateTimeOffset?)null}, {(DateTimeOffset?)null},
                  {(string?)null}, {(string?)null});
             """);
        var globallyHeld = await target.ApplyAsync(heldPermit, CancellationToken.None);
        Assert.True(globallyHeld.IsFailure);
        Assert.Equal("forms_retention_held", globallyHeld.Error);
        var releasedGlobalHold = await retentionStore.ReleaseAsync(
            holdId,
            "site-admin",
            "synthetic release",
            Now,
            CancellationToken.None);
        Assert.True(releasedGlobalHold.IsSuccess);
        dueEntity.PlaceLegalHold();
        await context.SaveChangesAsync();
        var held = await target.ApplyAsync(heldPermit, CancellationToken.None);
        Assert.True(held.IsFailure);
        Assert.Equal("forms_retention_legal_hold", held.Error);
        dueEntity.ReleaseLegalHold();
        await context.SaveChangesAsync();
        var applied = await target.ApplyAsync(
            heldPermit,
            CancellationToken.None);
        Assert.True(applied.IsSuccess);
        Assert.Equal(
            FormRetentionStatus.Anonymized,
            (await context.Set<FormSubmission>()
                .AsNoTracking()
                .SingleAsync(submission => submission.Id == dueEntity.Id))
            .RetentionStatus);

        var expiredEntity = futureEntity;
        var expiredPermit = await CreateRetentionPermitAsync(
            retentionStore,
            expiredEntity,
            Now,
            "forms-expired",
            operationTimeout: TimeSpan.FromSeconds(1));
        clock.SetUtcNow(Now.AddSeconds(2));
        var expired = await target.ApplyAsync(expiredPermit, CancellationToken.None);
        Assert.True(expired.IsFailure);
        Assert.Equal("retention_apply_permit_refused", expired.Error);

        var activeSubmission = new FormSubmission(
            definition.DefinitionVersionId,
            Enumerable.Repeat((byte)16, 32).ToArray(),
            Enumerable.Repeat((byte)17, 32).ToArray(),
            Now.AddDays(-100),
            Now.AddDays(-100),
            Now.AddDays(-10),
            definition.ConsentVersion);
        context.Add(activeSubmission);
        await context.SaveChangesAsync();
        clock.SetUtcNow(Now);
        var activePermit = await CreateRetentionPermitAsync(
            retentionStore,
            activeSubmission,
            Now,
            "forms-active-status");
        var wrongStatus = await target.ApplyAsync(activePermit, CancellationToken.None);
        Assert.True(wrongStatus.IsFailure);
        Assert.Equal("forms_retention_status_conflict", wrongStatus.Error);
        Assert.NotEqual(FormRetentionStatus.Anonymized, activeSubmission.RetentionStatus);
    }

    [Fact]
    public async Task ReclaimedRetentionPermitMutatesZeroAndCurrentPermitAppliesExactlyOnce()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ReclaimedRetentionPermitMutatesZeroAndCurrentPermitAppliesExactlyOnce));
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(
                definition,
                Enumerable.Repeat((byte)18, 32).ToArray()) with
            {
                AcceptedAtUtc = Now.AddDays(-100),
            });
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var submission = await context.Set<FormSubmission>()
            .SingleAsync(candidate => candidate.Id == submitted.Success.SubmissionId);
        submission.MarkRetentionEligible();
        await context.SaveChangesAsync();
        var clock = new SettableTimeProvider(Now);
        var retentionStore = new EfRetentionStore(context);
        var candidate = new RetentionCandidate(
            FormSubmissionRetentionTarget.TargetName,
            submission.Id.ToString("N"),
            submission.RetentionEligibleAtUtc);
        var request = new RetentionRunRequest(
            "forms-reclaimed-run",
            new RetentionPolicy(
                "forms-reclaimed-policy",
                FormSubmissionRetentionTarget.TargetName,
                TimeSpan.FromDays(90),
                10,
                TimeSpan.FromSeconds(1)),
            RetentionMode.Apply,
            "forms-test",
            "forms-reclaimed-run");
        var originalReservation = await retentionStore.TryStartAsync(
            request,
            Now,
            CancellationToken.None);
        Assert.True(
            originalReservation.IsSuccess,
            originalReservation.IsFailure ? originalReservation.Error : string.Empty);
        Assert.True((await retentionStore.GetOrCreateBatchAsync(
            originalReservation.Success.RunId,
            originalReservation.Success.LeaseToken,
            [candidate],
            Now,
            CancellationToken.None)).IsSuccess);
        var originalAuthorization = await retentionStore.TryBeginApplyAsync(
            originalReservation.Success.RunId,
            originalReservation.Success.LeaseToken,
            candidate,
            Now,
            CancellationToken.None);
        Assert.True(
            originalAuthorization.IsSuccess,
            originalAuthorization.IsFailure ? originalAuthorization.Error : string.Empty);
        var stalePermit = originalAuthorization.Success.Permit!;

        var reclaimedAt = Now.AddSeconds(2);
        clock.SetUtcNow(reclaimedAt);
        await using var reclaimedContext = database.CreateContext();
        var reclaimedStore = new EfRetentionStore(reclaimedContext);
        var target = new FormSubmissionRetentionTarget(reclaimedContext, clock);
        var reclaimed = await reclaimedStore.TryStartAsync(
            request,
            reclaimedAt,
            CancellationToken.None);
        Assert.True(
            reclaimed.IsSuccess,
            reclaimed.IsFailure ? reclaimed.Error : string.Empty);
        Assert.True(reclaimed.Success.CanExecute);
        Assert.NotEqual(stalePermit.LeaseToken, reclaimed.Success.LeaseToken);

        var stale = await target.ApplyAsync(stalePermit, CancellationToken.None);
        Assert.True(stale.IsFailure);
        Assert.Equal("retention_apply_permit_refused", stale.Error);
        Assert.Equal(
            "person@example.test",
            await reclaimedContext.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == submission.Id)
                .Select(value => value.Value)
                .SingleAsync());

        var currentAuthorization = await reclaimedStore.TryBeginApplyAsync(
            reclaimed.Success.RunId,
            reclaimed.Success.LeaseToken,
            candidate,
            reclaimedAt,
            CancellationToken.None);
        Assert.True(
            currentAuthorization.IsSuccess,
            currentAuthorization.IsFailure ? currentAuthorization.Error : string.Empty);
        var currentPermit = currentAuthorization.Success.Permit!;
        var applied = await target.ApplyAsync(currentPermit, CancellationToken.None);
        var appliedRowVersion = await ReadSubmissionRowVersionAsync(
            reclaimedContext,
            submission.Id);
        var repeated = await target.ApplyAsync(currentPermit, CancellationToken.None);
        var repeatedRowVersion = await ReadSubmissionRowVersionAsync(
            reclaimedContext,
            submission.Id);

        Assert.True(
            applied.IsSuccess,
            applied.IsFailure ? $"apply:{applied.Error}" : string.Empty);
        Assert.True(
            repeated.IsSuccess,
            repeated.IsFailure ? $"repeat:{repeated.Error}" : string.Empty);
        Assert.Equal(appliedRowVersion.Value.ToArray(), repeatedRowVersion.Value.ToArray());
        Assert.Equal(
            string.Empty,
            await reclaimedContext.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == submission.Id)
                .Select(value => value.Value)
                .SingleAsync());
        var markedApplied = await reclaimedStore.MarkAppliedAsync(
            currentPermit.RunId,
            currentPermit.LeaseToken,
            currentPermit.Candidate,
            reclaimedAt,
            CancellationToken.None);
        Assert.True(
            markedApplied.IsSuccess,
            markedApplied.IsFailure ? markedApplied.Error : string.Empty);
    }

    [Fact]
    public async Task T19RetentionWorkflowAnonymizesAndMarksApplied()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(T19RetentionWorkflowAnonymizesAndMarksApplied));
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(
                definition,
                Enumerable.Repeat((byte)19, 32).ToArray()) with
            {
                AcceptedAtUtc = Now.AddDays(-100),
            });
        Assert.True(submitted.IsSuccess);

        await using var context = database.CreateContext();
        var submission = await context.Set<FormSubmission>()
            .SingleAsync(candidate => candidate.Id == submitted.Success.SubmissionId);
        submission.MarkRetentionEligible();
        await context.SaveChangesAsync();
        var clock = new SettableTimeProvider(Now);
        var retentionStore = new EfRetentionStore(context);
        var target = new FormSubmissionRetentionTarget(context, clock);
        var workflow = new RetentionWorkflow([target], retentionStore, clock);

        var result = await workflow.ExecuteAsync(
            new RetentionRunRequest(
                "forms-workflow-apply",
                new RetentionPolicy(
                    "forms-workflow-policy",
                    FormSubmissionRetentionTarget.TargetName,
                    TimeSpan.FromDays(90),
                    10,
                    TimeSpan.FromMinutes(5)),
                RetentionMode.Apply,
                "forms-test",
                "forms-workflow-apply"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Success.Applied);
        Assert.Equal(0, result.Success.Held);
        Assert.Equal(
            FormRetentionStatus.Anonymized,
            (await context.Set<FormSubmission>()
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == submission.Id))
            .RetentionStatus);
        Assert.Equal(
            string.Empty,
            await context.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == submission.Id)
                .Select(value => value.Value)
                .SingleAsync());
    }

    [Fact]
    public async Task WaitingWorkerWithReclaimedExpiredLeaseMutatesZero()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(WaitingWorkerWithReclaimedExpiredLeaseMutatesZero));
        var scenario = await CreateRetentionScenarioAsync(
            database,
            fingerprintByte: 21,
            operationTimeout: TimeSpan.FromMinutes(1));
        var clock = new SettableTimeProvider(Now);
        var interceptor = new RetentionLockWaitInterceptor();
        await using var targetContext = database.CreateContext(interceptor);
        var target = new FormSubmissionRetentionTarget(targetContext, clock);
        await using var blocker = await RetentionTargetLock.AcquireAsync(
            database.ConnectionString);

        var apply = target.ApplyAsync(scenario.Permit, CancellationToken.None);
        await interceptor.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, clock.ReadCount);
        var takeoverAt = Now.AddMinutes(2);
        clock.SetUtcNow(takeoverAt);
        await blocker.TakeOverAsync(
            scenario.Permit.RunId,
            Guid.NewGuid(),
            takeoverAt.AddMinutes(5));
        await blocker.ReleaseAsync();

        var result = await apply;

        Assert.True(result.IsFailure);
        Assert.Equal("retention_apply_permit_refused", result.Error);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(
            "person@example.test",
            await targetContext.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == scenario.SubmissionId)
                .Select(value => value.Value)
                .SingleAsync());
        Assert.Equal(
            FormRetentionStatus.Eligible,
            await targetContext.Set<FormSubmission>()
                .Where(submission => submission.Id == scenario.SubmissionId)
                .Select(submission => submission.RetentionStatus)
                .SingleAsync());
    }

    [Fact]
    public async Task HoldCommittedWhileWorkerWaitsIsObservedAfterLock()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(HoldCommittedWhileWorkerWaitsIsObservedAfterLock));
        var scenario = await CreateRetentionScenarioAsync(
            database,
            fingerprintByte: 22,
            operationTimeout: TimeSpan.FromMinutes(5));
        var clock = new SettableTimeProvider(Now);
        var interceptor = new RetentionLockWaitInterceptor();
        await using var targetContext = database.CreateContext(interceptor);
        var target = new FormSubmissionRetentionTarget(targetContext, clock);
        await using var blocker = await RetentionTargetLock.AcquireAsync(
            database.ConnectionString);

        var apply = target.ApplyAsync(scenario.Permit, CancellationToken.None);
        await interceptor.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, clock.ReadCount);
        var holdStartsAt = Now.AddMinutes(1);
        clock.SetUtcNow(holdStartsAt);
        await blocker.InsertHoldAsync(
            scenario.SubmissionId,
            holdStartsAt);
        await blocker.ReleaseAsync();

        var result = await apply;

        Assert.True(result.IsFailure);
        Assert.Equal("forms_retention_held", result.Error);
        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(
            "person@example.test",
            await targetContext.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == scenario.SubmissionId)
                .Select(value => value.Value)
                .SingleAsync());
    }

    [Fact]
    public async Task CurrentUnexpiredPermitWithoutHoldMutatesExactlyOnce()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(CurrentUnexpiredPermitWithoutHoldMutatesExactlyOnce));
        var scenario = await CreateRetentionScenarioAsync(
            database,
            fingerprintByte: 23,
            operationTimeout: TimeSpan.FromMinutes(5));
        var clock = new SettableTimeProvider(Now);
        await using var context = database.CreateContext();
        var target = new FormSubmissionRetentionTarget(context, clock);

        var first = await target.ApplyAsync(scenario.Permit, CancellationToken.None);
        var firstRowVersion = await ReadSubmissionRowVersionAsync(
            context,
            scenario.SubmissionId);
        var second = await target.ApplyAsync(scenario.Permit, CancellationToken.None);
        var secondRowVersion = await ReadSubmissionRowVersionAsync(
            context,
            scenario.SubmissionId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(firstRowVersion.Value.ToArray(), secondRowVersion.Value.ToArray());
        Assert.Equal(
            string.Empty,
            await context.Set<FormSubmissionValue>()
                .Where(value => value.SubmissionId == scenario.SubmissionId)
                .Select(value => value.Value)
                .SingleAsync());
        var marked = await new EfRetentionStore(context).MarkAppliedAsync(
            scenario.Permit.RunId,
            scenario.Permit.LeaseToken,
            scenario.Permit.Candidate,
            Now,
            CancellationToken.None);
        Assert.True(marked.IsSuccess);
    }

    private static async Task<SqlServerTestDatabase> CreateDatabaseAsync(string name)
    {
        var database = await SqlServerTestDatabase.CreateAsync(name);
        await using var context = database.CreateContext();
        var registered = await new EfDurableJobStore(context).RegisterDefinitionAsync(
            new JobDefinitionRegistration(
                FormDeliveryJobHandler.DefinitionKey,
                FormDeliveryJobHandler.Name,
                5,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromHours(1)),
            CancellationToken.None);
        Assert.True(registered.IsSuccess);
        return database;
    }

    private static async Task<FormDefinitionView> SeedDefinitionAsync(
        SqlServerTestDatabase database)
    {
        await using var context = database.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var definition = new FormDefinition("test-contact-v1", "Synthetic contact");
        context.Add(definition);
        await context.SaveChangesAsync();
        var version = new FormDefinitionVersion(
            definition.Id,
            1,
            "test-destination",
            "test-template",
            "test-consent-v1",
            Now);
        var field = new FormField(
            version.Id,
            "email",
            "Email",
            FormFieldKind.Email,
            true,
            3,
            320,
            null,
            null,
            FormPatternKind.Email,
            [],
            1,
            FormPrivacyClass.Contact,
            Now);
        context.Add(version);
        context.Add(field);
        await context.SaveChangesAsync();
        definition.Publish(version);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        var stateRowVersion = context.Entry(definition)
            .Property<byte[]>(PersistencePropertyNames.RowVersion)
            .CurrentValue ?? [];
        return new FormDefinitionView(
            definition.Id,
            definition.Key,
            definition.Title,
            version.Id,
            version.Version,
            version.ConsentVersion,
            [
                new FormFieldView(
                    field.Id,
                    version.Id,
                    field.Key,
                    field.Label,
                    field.Kind,
                    field.Required,
                    field.MinimumLength,
                    field.MaximumLength,
                    field.MinimumValue,
                    field.MaximumValue,
                    field.PatternKind,
                    field.GetChoices(),
                    field.Order,
                    field.PrivacyClass),
            ],
            new RowVersion(stateRowVersion));
    }

    private static ValidatedFormSubmission CreateSubmission(
        FormDefinitionView definition,
        byte[] fingerprint) =>
        new(
            definition,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["email"] = "person@example.test",
            },
            Enumerable.Repeat((byte)2, 32).ToArray(),
            fingerprint,
            "correlation-forms",
            Now);

    private static async Task<Result<FormReceipt, FormError>> SubmitAfterSignalAsync(
        SqlServerTestDatabase database,
        ValidatedFormSubmission request,
        Task start)
    {
        await start;
        return await SubmitAsync(database, request);
    }

    private static async Task<Result<FormReceipt, FormError>> SubmitAsync(
        SqlServerTestDatabase database,
        ValidatedFormSubmission request)
    {
        await using var context = database.CreateContext();
        return await CreateStore(context, CreateOptions()).SubmitAsync(
            request,
            CancellationToken.None);
    }

    private static async Task<FormRateLimitDecision> AttemptRateLimitAsync(
        SqlServerTestDatabase database,
        FormsOptions options,
        TimeProvider timeProvider,
        byte[] fingerprint)
    {
        await using var context = database.CreateContext();
        var limiter = new SqlFormsRateLimiter(
            context,
            options,
            timeProvider,
            NullLogger<SqlFormsRateLimiter>.Instance);
        return await limiter.AttemptAsync(
            "test-contact-v1",
            fingerprint,
            CancellationToken.None);
    }

    private static async Task<RowVersion> ReadSubmissionRowVersionAsync(
        TestHusayniaDbContext context,
        Guid submissionId) =>
        new(await context.Set<FormSubmission>()
            .AsNoTracking()
            .Where(submission => submission.Id == submissionId)
            .Select(submission => EF.Property<byte[]>(
                submission,
                PersistencePropertyNames.RowVersion))
            .SingleAsync());

    private static async Task<RetentionApplyPermit> CreateRetentionPermitAsync(
        EfRetentionStore retentionStore,
        FormSubmission submission,
        DateTimeOffset now,
        string idempotencyKey,
        TimeSpan? operationTimeout = null)
    {
        var candidate = new RetentionCandidate(
            FormSubmissionRetentionTarget.TargetName,
            submission.Id.ToString("N"),
            submission.RetentionEligibleAtUtc);
        var reservation = await retentionStore.TryStartAsync(
            new RetentionRunRequest(
                idempotencyKey,
                new RetentionPolicy(
                    "forms-test-policy",
                    FormSubmissionRetentionTarget.TargetName,
                    TimeSpan.FromDays(90),
                    10,
                    operationTimeout ?? TimeSpan.FromMinutes(5)),
                RetentionMode.Apply,
                "forms-test",
                idempotencyKey),
            now,
            CancellationToken.None);
        Assert.True(reservation.IsSuccess);
        var batch = await retentionStore.GetOrCreateBatchAsync(
            reservation.Success.RunId,
            reservation.Success.LeaseToken,
            [candidate],
            now,
            CancellationToken.None);
        Assert.True(batch.IsSuccess);
        var authorization = await retentionStore.TryBeginApplyAsync(
            reservation.Success.RunId,
            reservation.Success.LeaseToken,
            candidate,
            now,
            CancellationToken.None);
        Assert.True(authorization.IsSuccess);
        Assert.Equal(RetentionApplyDecision.Apply, authorization.Success.Decision);
        Assert.NotNull(authorization.Success.Permit);
        return authorization.Success.Permit!;
    }

    private static async Task<RetentionScenario> CreateRetentionScenarioAsync(
        SqlServerTestDatabase database,
        byte fingerprintByte,
        TimeSpan operationTimeout)
    {
        var definition = await SeedDefinitionAsync(database);
        var submitted = await SubmitAsync(
            database,
            CreateSubmission(
                definition,
                Enumerable.Repeat(fingerprintByte, 32).ToArray()) with
            {
                AcceptedAtUtc = Now.AddDays(-100),
            });
        Assert.True(submitted.IsSuccess);
        await using var context = database.CreateContext();
        var submission = await context.Set<FormSubmission>()
            .SingleAsync(candidate => candidate.Id == submitted.Success.SubmissionId);
        submission.MarkRetentionEligible();
        await context.SaveChangesAsync();
        var retentionStore = new EfRetentionStore(context);
        var permit = await CreateRetentionPermitAsync(
            retentionStore,
            submission,
            Now,
            $"forms-contention-{fingerprintByte}",
            operationTimeout);
        return new RetentionScenario(submission.Id, permit);
    }

    private static EfFormsStore CreateStore(
        TestHusayniaDbContext context,
        FormsOptions options)
    {
        var time = new FixedTimeProvider(Now);
        return new EfFormsStore(
            context,
            options,
            time,
            new HostileSensitiveDataRedactor(),
            new EfDurableJobStore(context, time),
            new NoopAuditWriter(),
            new NoopAuditFinalizer());
    }

    private static FormsOptions CreateOptions(
        int permitLimit = 10,
        FormDeliveryMode deliveryMode = FormDeliveryMode.Disabled,
        string? pickupDirectory = null) =>
        new()
        {
            Enabled = true,
            FingerprintKey = new byte[32],
            DuplicateWindow = TimeSpan.FromMinutes(15),
            RateLimitPermitLimit = permitLimit,
            RateLimitWindow = TimeSpan.FromMinutes(5),
            RateLimitRetention = TimeSpan.FromDays(1),
            RetentionPeriod = TimeSpan.FromDays(90),
            DeliveryMode = deliveryMode,
            PickupDirectory = pickupDirectory ?? string.Empty,
            PickupDestinationKey = "test-destination",
        };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        private int readCount;

        internal int ReadCount => Volatile.Read(ref readCount);

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref readCount);
            return now;
        }

        internal void SetUtcNow(DateTimeOffset value) => now = value;
    }

    private sealed class NoopAuditWriter : IAuditWriter
    {
        public Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CancellingSender : IOutboundMessageSender
    {
        public Task<Result<OutboundMessageReceipt, IntegrationError>> SendAsync(
            OutboundMessage message,
            CancellationToken ct) =>
            throw new OperationCanceledException();
    }

    private sealed class RecordingSender : IOutboundMessageSender
    {
        internal int CallCount { get; private set; }

        public Task<Result<OutboundMessageReceipt, IntegrationError>> SendAsync(
            OutboundMessage message,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(
                Result.Succeed<OutboundMessageReceipt, IntegrationError>(
                    new OutboundMessageReceipt("synthetic", Now)));
        }
    }

    private sealed class NoopAuditFinalizer : IIdentityAuditFinalizer
    {
        public Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null) =>
            completePersistenceAsync?.Invoke(CancellationToken.None) ??
            Task.CompletedTask;
    }

    private sealed class ThrowingAuditFinalizer : IIdentityAuditFinalizer
    {
        internal int CallCount { get; private set; }

        public Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            CallCount++;
            throw new IdentityAuditFinalizationException("synthetic audit failure");
        }
    }

    private sealed class BlockingAuditFinalizer : IIdentityAuditFinalizer
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            Entered.TrySetResult();
            await Release.Task;
            if (completePersistenceAsync is not null)
            {
                await completePersistenceAsync(CancellationToken.None);
            }
        }
    }

    private sealed record RetentionScenario(
        Guid SubmissionId,
        RetentionApplyPermit Permit);

    private sealed class RetentionLockWaitInterceptor : DbCommandInterceptor
    {
        internal TaskCompletionSource Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "sys.sp_getapplock",
                    StringComparison.Ordinal))
            {
                Requested.TrySetResult();
            }

            return base.NonQueryExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class RetentionTargetLock : IAsyncDisposable
    {
        private readonly SqlConnection connection;
        private readonly SqlTransaction transaction;
        private bool released;

        private RetentionTargetLock(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        internal static async Task<RetentionTargetLock> AcquireAsync(
            string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @resource,
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 10000;
                SELECT @result;
                """;
            command.Parameters.Add(
                new SqlParameter("@resource", SqlDbType.NVarChar, 255)
                {
                    Value = RetentionTargetResource,
                });
            var result = Convert.ToInt32(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(result >= 0);
            return new RetentionTargetLock(connection, transaction);
        }

        internal async Task TakeOverAsync(
            Guid runId,
            Guid leaseToken,
            DateTimeOffset leaseExpiresAtUtc)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE [dbo].[OperationsRetentionRuns]
                SET [LeaseToken] = @leaseToken,
                    [LeaseExpiresAtUtc] = @leaseExpiresAtUtc
                WHERE [Id] = @runId;

                UPDATE [dbo].[OperationsRetentionRunItems]
                SET [Status] = N'Pending',
                    [UpdatedAtUtc] = @updatedAtUtc
                WHERE [RunId] = @runId
                  AND [Status] = N'Applying';
                """;
            command.Parameters.Add(new SqlParameter("@leaseToken", SqlDbType.UniqueIdentifier)
            {
                Value = leaseToken,
            });
            command.Parameters.Add(new SqlParameter("@leaseExpiresAtUtc", SqlDbType.DateTimeOffset)
            {
                Value = leaseExpiresAtUtc,
            });
            command.Parameters.Add(new SqlParameter("@updatedAtUtc", SqlDbType.DateTimeOffset)
            {
                Value = leaseExpiresAtUtc.AddMinutes(-5),
            });
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier)
            {
                Value = runId,
            });
            Assert.Equal(2, await command.ExecuteNonQueryAsync());
        }

        internal async Task InsertHoldAsync(
            Guid submissionId,
            DateTimeOffset startsAtUtc)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO [dbo].[OperationsRetentionHolds]
                    ([Id], [IdempotencyKey], [Target], [SubjectId], [Kind], [Actor], [Reason],
                     [StartsAtUtc], [ExpiresAtUtc], [ReleasedAtUtc], [ReleasedBy], [ReleaseReason])
                VALUES
                    (@id, @idempotencyKey, @target, @subjectId, N'Legal', N'site-admin',
                     N'contention hold', @startsAtUtc, NULL, NULL, NULL, NULL);
                """;
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier)
            {
                Value = Guid.NewGuid(),
            });
            command.Parameters.Add(new SqlParameter("@idempotencyKey", SqlDbType.NVarChar, 256)
            {
                Value = $"forms-contention-hold-{submissionId:N}",
            });
            command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, 100)
            {
                Value = FormSubmissionRetentionTarget.TargetName,
            });
            command.Parameters.Add(new SqlParameter("@subjectId", SqlDbType.NVarChar, 256)
            {
                Value = submissionId.ToString("N"),
            });
            command.Parameters.Add(new SqlParameter("@startsAtUtc", SqlDbType.DateTimeOffset)
            {
                Value = startsAtUtc,
            });
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        internal async Task ReleaseAsync()
        {
            await transaction.CommitAsync();
            released = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!released)
            {
                await transaction.RollbackAsync();
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static string RetentionTargetResource =>
            $"retention-target:{Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(FormSubmissionRetentionTarget.TargetName)))}";
    }
}
