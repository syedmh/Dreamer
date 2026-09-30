using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Forms;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Forms;

public sealed class EfFormsStore(
    HusayniaDbContext dbContext,
    FormsOptions options,
    TimeProvider timeProvider,
    ISensitiveDataRedactor redactor,
    IDurableJobStore jobStore,
    IAuditWriter auditWriter,
    IIdentityAuditFinalizer retryAuditFinalizer)
    : IActiveFormDefinitionReader,
      IFormSubmissionStore,
      IFormDeliveryStore,
      IFormAdministrationStore
{
    private const int MaximumSubmissionAttempts = 4;
    private const string SubmissionAcceptAction = "forms.submission.accept";
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly FormsOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly FormsAuditAppender auditAppender =
        new(
            dbContext ?? throw new ArgumentNullException(nameof(dbContext)),
            redactor ?? throw new ArgumentNullException(nameof(redactor)),
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
    private readonly IDurableJobStore jobStore =
        jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    private readonly IAuditWriter auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly IIdentityAuditFinalizer retryAuditFinalizer =
        retryAuditFinalizer ?? throw new ArgumentNullException(nameof(retryAuditFinalizer));

    public async Task<Result<FormDefinitionView, FormError>> GetActiveAsync(
        string formKey,
        CancellationToken cancellationToken)
    {
        if (!FormSubmissionService.TryNormalizeFormKey(formKey, out var normalized))
        {
            return Failure<FormDefinitionView>(
                "form_not_found",
                "The requested form is not available.");
        }

        var view = await LoadActiveDefinitionAsync(normalized, cancellationToken)
            .ConfigureAwait(false);
        return view is null
            ? Failure<FormDefinitionView>(
                "form_not_found",
                "The requested form is not available.")
            : Result.Succeed<FormDefinitionView, FormError>(view);
    }

    public async Task<Result<FormReceipt, FormError>> SubmitAsync(
        ValidatedFormSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        for (var attempt = 1; attempt <= MaximumSubmissionAttempts; attempt++)
        {
            try
            {
                return await SubmitOnceAsync(submission, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SqlException exception)
                when (exception.Number == 1205 && attempt < MaximumSubmissionAttempts)
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(
                        TimeSpan.FromMilliseconds(5 * attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
                when (IsDeadlock(exception) && attempt < MaximumSubmissionAttempts)
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(
                        TimeSpan.FromMilliseconds(5 * attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsSqlError(exception, 1205) &&
                    attempt < MaximumSubmissionAttempts)
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(
                        TimeSpan.FromMilliseconds(5 * attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
                when (IsCompletedTransaction(exception) &&
                    attempt < MaximumSubmissionAttempts)
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(
                        TimeSpan.FromMilliseconds(5 * attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
                when (IsUniqueConstraintViolation(exception))
            {
                dbContext.ChangeTracker.Clear();
                return await ResolveDuplicateAsync(submission, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (IsSqlError(exception, 2601) || IsSqlError(exception, 2627))
            {
                dbContext.ChangeTracker.Clear();
                return await ResolveDuplicateAsync(submission, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbException)
            {
                return await AuditSubmissionFailureAsync(
                        submission,
                        "forms_persistence_failure")
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                return await AuditSubmissionFailureAsync(
                        submission,
                        "forms_persistence_failure")
                    .ConfigureAwait(false);
            }
        }

        return await AuditSubmissionFailureAsync(
                submission,
                "forms_persistence_failure")
            .ConfigureAwait(false);
    }

    public async Task<Result<FormDeliveryMessage, FormError>> BeginAttemptAsync(
        Guid submissionId,
        Guid definitionVersionId,
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var submission = await dbContext.Set<FormSubmission>()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Id == submissionId &&
                        candidate.DefinitionVersionId == definitionVersionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (submission is null ||
                submission.RetentionStatus == FormRetentionStatus.Anonymized)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<FormDeliveryMessage>(
                    "submission_not_deliverable",
                    "The form submission is not available for delivery.");
            }

            if (submission.DeliveryJobInstanceId != context.JobInstanceId)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<FormDeliveryMessage>(
                    "delivery_job_mismatch",
                    "The delivery job is not associated with the form submission.");
            }

            var version = await dbContext.Set<FormDefinitionVersion>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == definitionVersionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (version is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<FormDeliveryMessage>(
                    "definition_version_not_found",
                    "The form definition version is not available.");
            }

            var existing = await dbContext.Set<FormDeliveryAttempt>()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.SubmissionId == submissionId &&
                        candidate.JobInstanceId == context.JobInstanceId &&
                        candidate.AttemptNumber == context.AttemptNumber,
                    cancellationToken)
                .ConfigureAwait(false);
            var attempt = existing ??
                new FormDeliveryAttempt(
                    submissionId,
                    context.JobInstanceId,
                    context.AttemptNumber,
                    timeProvider.GetUtcNow().ToUniversalTime());
            if (existing is null)
            {
                dbContext.Add(attempt);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (existing.Outcome != FormDeliveryAttemptOutcome.Started)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<FormDeliveryMessage>(
                    "attempt_already_finalized",
                    "The form delivery attempt was already finalized.");
            }

            var values = await dbContext.Set<FormSubmissionValue>()
                .AsNoTracking()
                .Where(candidate => candidate.SubmissionId == submissionId)
                .OrderBy(candidate => candidate.FieldKey)
                .ToDictionaryAsync(
                    candidate => candidate.FieldKey,
                    candidate => candidate.Value,
                    StringComparer.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<FormDeliveryMessage, FormError>(
                new FormDeliveryMessage(
                    attempt.Id,
                    version.DestinationKey,
                    version.TemplateKey,
                    values));
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure<FormDeliveryMessage>(
                "delivery_store_unavailable",
                "The form delivery store is unavailable.");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure<FormDeliveryMessage>(
                "delivery_store_unavailable",
                "The form delivery store is unavailable.");
        }
    }

    public async Task<Result<bool, FormError>> CompleteAttemptAsync(
        Guid attemptId,
        bool succeeded,
        string? errorCode,
        byte[]? providerReceiptHash,
        bool cancelled,
        CancellationToken cancellationToken)
    {
        try
        {
            var attempt = await dbContext.Set<FormDeliveryAttempt>()
                .SingleOrDefaultAsync(candidate => candidate.Id == attemptId, cancellationToken)
                .ConfigureAwait(false);
            if (attempt is null)
            {
                return Failure<bool>(
                    "delivery_attempt_not_found",
                    "The form delivery attempt was not found.");
            }

            if (attempt.Outcome != FormDeliveryAttemptOutcome.Started)
            {
                return Result.Succeed<bool, FormError>(
                    succeeded && attempt.Outcome == FormDeliveryAttemptOutcome.Succeeded ||
                    !succeeded && attempt.Outcome is
                        FormDeliveryAttemptOutcome.Failed or
                        FormDeliveryAttemptOutcome.Cancelled);
            }

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            if (cancelled)
            {
                attempt.CompleteCancellation(now);
            }
            else if (succeeded && providerReceiptHash is { Length: 32 })
            {
                attempt.CompleteSuccess(providerReceiptHash, now);
            }
            else
            {
                attempt.CompleteFailure(errorCode ?? "sender_failure", now);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<bool, FormError>(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure<bool>(
                "delivery_attempt_conflict",
                "The form delivery attempt changed concurrently.");
        }
        catch (DbException)
        {
            return Failure<bool>(
                "delivery_store_unavailable",
                "The form delivery store is unavailable.");
        }
        catch (DbUpdateException)
        {
            return Failure<bool>(
                "delivery_store_unavailable",
                "The form delivery store is unavailable.");
        }
    }

    public async Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
        string formKey,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var view = await LoadActiveDefinitionAsync(formKey, cancellationToken)
            .ConfigureAwait(false);
        auditAppender.Stage(
            audit,
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["result"] = view is null ? "not_found" : "read",
            });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return view is null
            ? Failure<FormDefinitionView>(
                "form_not_found",
                "The requested form definition was not found.")
            : Result.Succeed<FormDefinitionView, FormError>(view);
    }

    public async Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
        PublishFormDefinitionCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var formKey = command.FormKey.Trim();
            var definition = await dbContext.Set<FormDefinition>()
                .SingleOrDefaultAsync(candidate => candidate.Key == formKey, cancellationToken)
                .ConfigureAwait(false);
            if (definition is null)
            {
                if (!command.ExpectedStateRowVersion.Value.IsEmpty)
                {
                    auditAppender.Stage(
                        audit,
                        PrivilegedAttemptOutcome.Allowed,
                        new Dictionary<string, string?>
                        {
                            ["result"] = "conflict",
                            ["errorCode"] = "definition_conflict",
                        });
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Failure<FormDefinitionPublicationReceipt>(
                        "definition_conflict",
                        "The form definition state changed; reload it and retry.");
                }

                definition = new FormDefinition(formKey, command.Title);
                dbContext.Add(definition);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var entry = dbContext.Entry(definition);
                var currentRowVersion = entry
                    .Property<byte[]>(PersistencePropertyNames.RowVersion)
                    .CurrentValue ?? [];
                if (command.ExpectedStateRowVersion.Value.IsEmpty ||
                    !CryptographicOperations.FixedTimeEquals(
                        currentRowVersion,
                        command.ExpectedStateRowVersion.Value.Span))
                {
                    auditAppender.Stage(
                        audit with { TargetId = definition.Id.ToString("N") },
                        PrivilegedAttemptOutcome.Allowed,
                        new Dictionary<string, string?>
                        {
                            ["result"] = "conflict",
                            ["errorCode"] = "definition_conflict",
                        });
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Failure<FormDefinitionPublicationReceipt>(
                        "definition_conflict",
                        "The form definition state changed; reload it and retry.");
                }

                entry.Property<byte[]>(PersistencePropertyNames.RowVersion).OriginalValue =
                    command.ExpectedStateRowVersion.Value.ToArray();
                definition.Rename(command.Title);
            }

            var nextVersion = (await dbContext.Set<FormDefinitionVersion>()
                .Where(candidate => candidate.DefinitionId == definition.Id)
                .MaxAsync(candidate => (int?)candidate.Version, cancellationToken)
                .ConfigureAwait(false) ?? 0) + 1;
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var version = new FormDefinitionVersion(
                definition.Id,
                nextVersion,
                command.DestinationKey,
                command.TemplateKey,
                command.ConsentVersion,
                now);
            var fields = command.Fields
                .Select(field => new FormField(
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
                    field.Choices,
                    field.Order,
                    field.PrivacyClass,
                    now))
                .ToArray();
            dbContext.Add(version);
            dbContext.AddRange(fields);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            definition.Publish(version);
            auditAppender.Stage(
                audit with { TargetId = definition.Id.ToString("N") },
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "published",
                    ["version"] = nextVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["fieldCount"] = fields.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            var stateRowVersion = dbContext.Entry(definition)
                .Property<byte[]>(PersistencePropertyNames.RowVersion)
                .CurrentValue ?? [];
            return Result.Succeed<FormDefinitionPublicationReceipt, FormError>(
                new FormDefinitionPublicationReceipt(
                    definition.Id,
                    version.Id,
                    version.Version,
                    now,
                    new RowVersion(stateRowVersion)));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<FormDefinitionPublicationReceipt>(
                    audit,
                    "definition_conflict",
                    "The form definition state changed; reload it and retry.")
                .ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<FormDefinitionPublicationReceipt>(
                    audit,
                    "definition_conflict",
                    "The form definition changed concurrently.")
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<FormDefinitionPublicationReceipt>(
                    audit,
                    "forms_persistence_failure",
                    "The form definition could not be persisted.")
                .ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<FormDefinitionPublicationReceipt>(
                    audit,
                    "forms_persistence_failure",
                    "The form definition could not be persisted.")
                .ConfigureAwait(false);
        }
    }

    public async Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
        int take,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var rows = await (
                    from submission in dbContext.Set<FormSubmission>().AsNoTracking()
                    join version in dbContext.Set<FormDefinitionVersion>().AsNoTracking()
                        on submission.DefinitionVersionId equals version.Id
                    join definition in dbContext.Set<FormDefinition>().AsNoTracking()
                        on version.DefinitionId equals definition.Id
                    orderby submission.AcceptedAtUtc descending, submission.Id descending
                    select new
                    {
                        Submission = submission,
                        Definition = definition,
                        Version = version,
                        StateRowVersion = EF.Property<byte[]>(
                            submission,
                            PersistencePropertyNames.RowVersion),
                    })
                .Take(take)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var jobIds = rows
                .Where(row => row.Submission.DeliveryJobInstanceId.HasValue)
                .Select(row => row.Submission.DeliveryJobInstanceId!.Value)
                .ToArray();
            var states = new Dictionary<Guid, string>();
            foreach (var jobId in jobIds)
            {
                var lookup = await jobStore.GetStateAsync(jobId, cancellationToken)
                    .ConfigureAwait(false);
                if (lookup.IsSuccess && lookup.Success.Snapshot is { } snapshot)
                {
                    states[jobId] = snapshot.State;
                }
            }
            var summaries = rows.Select(row => new FormSubmissionSummary(
                    row.Submission.Id,
                    row.Definition.Key,
                    row.Version.Version,
                    row.Submission.AcceptedAtUtc,
                    row.Submission.RetentionStatus,
                    row.Submission.HasLegalHold,
                    row.Submission.DeliveryJobInstanceId,
                    row.Submission.DeliveryJobInstanceId.HasValue &&
                    states.TryGetValue(row.Submission.DeliveryJobInstanceId.Value, out var state)
                        ? state
                        : "Unavailable",
                    new RowVersion(row.StateRowVersion)))
                .ToArray();
            auditAppender.Stage(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "read",
                    ["count"] = summaries.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<IReadOnlyList<FormSubmissionSummary>, FormError>(summaries);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<IReadOnlyList<FormSubmissionSummary>>(
                    audit,
                    "forms_persistence_failure",
                    "Submission summaries could not be read.")
                .ConfigureAwait(false);
        }
    }

    public async Task<Result<bool, FormError>> RetryDeliveryAsync(
        Guid submissionId,
        string reason,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        if (submissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length > 1_000)
        {
            return await AuditStoreFailureAsync<bool>(
                    audit,
                    "invalid_retry_request",
                    "A bounded delivery retry request is required.")
                .ConfigureAwait(false);
        }

        var jobId = await dbContext.Set<FormSubmission>()
            .AsNoTracking()
            .Where(candidate => candidate.Id == submissionId)
            .Select(candidate => candidate.DeliveryJobInstanceId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!jobId.HasValue)
        {
            return await AuditStoreFailureAsync<bool>(
                    audit,
                    "submission_not_found",
                    "The form submission or delivery job was not found.")
                .ConfigureAwait(false);
        }

        var safeReason = CreateReasonFingerprint(reason);
        var state = await jobStore.GetStateAsync(jobId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (state.IsFailure ||
            state.Success.Snapshot is not { State: "DeadLettered" })
        {
            return await AuditStoreFailureAsync<bool>(
                    audit,
                    "delivery_retry_unavailable",
                    "The delivery job is not eligible for retry.")
                .ConfigureAwait(false);
        }

        try
        {
            await retryAuditFinalizer.FinalizeOnceAsync(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "retry_authorized",
                        ["reasonHash"] = safeReason,
                    })
                .ConfigureAwait(false);
        }
        catch
        {
            return Failure<bool>(
                "forms_audit_unavailable",
                "The delivery retry was not authorized because its Forms audit could not be finalized.");
        }

        var requeue = await jobStore.RequeueDeadLetterAsync(
                jobId.Value,
                audit.ActorId ?? "site-administrator",
                safeReason,
                audit.CorrelationId,
                timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken)
            .ConfigureAwait(false);
        if (requeue.IsFailure || !requeue.Success)
        {
            return Failure<bool>(
                "delivery_retry_unavailable",
                "The audited delivery retry could not be applied.");
        }

        return Result.Succeed<bool, FormError>(true);
    }

    public async Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
        FormRetentionCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var submission = await dbContext.Set<FormSubmission>()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == command.SubmissionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (submission is null)
            {
                auditAppender.Stage(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "not_found",
                        ["errorCode"] = "submission_not_found",
                    });
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<FormRetentionReceipt>(
                    "submission_not_found",
                    "The form submission was not found.");
            }

            var entry = dbContext.Entry(submission);
            var currentRowVersion = entry
                .Property<byte[]>(PersistencePropertyNames.RowVersion)
                .CurrentValue ?? [];
            if (!CryptographicOperations.FixedTimeEquals(
                    currentRowVersion,
                    command.ExpectedStateRowVersion.Value.Span))
            {
                auditAppender.Stage(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "conflict",
                        ["errorCode"] = "retention_conflict",
                    });
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<FormRetentionReceipt>(
                    "retention_conflict",
                    "The form submission state changed; reload it and retry.");
            }

            entry.Property<byte[]>(PersistencePropertyNames.RowVersion).OriginalValue =
                command.ExpectedStateRowVersion.Value.ToArray();
            var error = ApplyRetentionAction(submission, command.Action);
            if (error is null && command.Action == FormRetentionAction.Anonymize)
            {
                var values = await dbContext.Set<FormSubmissionValue>()
                    .Where(candidate => candidate.SubmissionId == submission.Id)
                    .ToArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var value in values)
                {
                    value.Anonymize();
                }
            }

            auditAppender.Stage(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = error is null ? "changed" : "rejected",
                    ["action"] = command.Action.ToString(),
                    ["errorCode"] = error?.Code,
                    ["status"] = submission.RetentionStatus.ToString(),
                    ["legalHold"] = submission.HasLegalHold.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return Result.Fail<FormRetentionReceipt, FormError>(error);
            }

            var stateRowVersion = entry
                .Property<byte[]>(PersistencePropertyNames.RowVersion)
                .CurrentValue ?? [];
            return Result.Succeed<FormRetentionReceipt, FormError>(
                new FormRetentionReceipt(
                    submission.Id,
                    submission.RetentionStatus,
                    submission.HasLegalHold,
                    submission.AnonymizedAtUtc,
                    new RowVersion(stateRowVersion)));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<FormRetentionReceipt>(
                    audit,
                    "retention_conflict",
                    "The form submission changed concurrently.")
                .ConfigureAwait(false);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return await AuditStoreFailureAsync<FormRetentionReceipt>(
                    audit,
                    "forms_persistence_failure",
                    "The retention state could not be changed.")
                .ConfigureAwait(false);
        }
    }

    private async Task<Result<FormReceipt, FormError>> SubmitOnceAsync(
        ValidatedFormSubmission request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var active = await dbContext.Set<FormDefinition>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.Key == request.Definition.FormKey &&
                        candidate.IsEnabled &&
                        candidate.PublishedVersionId == request.Definition.DefinitionVersionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (active is null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<FormReceipt>(
                    "form_version_changed",
                    "The form changed; reload it and submit again.");
            }

            var windowStart = GetWindowStart(request.AcceptedAtUtc, options.DuplicateWindow);
            var existing = await dbContext.Set<FormSubmission>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.DefinitionVersionId ==
                            request.Definition.DefinitionVersionId &&
                        candidate.DuplicateFingerprint == request.DuplicateFingerprint &&
                        candidate.DuplicateWindowStartUtc == windowStart,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DuplicateResult(existing, request.CanonicalPayloadHash);
            }

            var submission = new FormSubmission(
                request.Definition.DefinitionVersionId,
                request.DuplicateFingerprint,
                request.CanonicalPayloadHash,
                windowStart,
                request.AcceptedAtUtc,
                request.AcceptedAtUtc.Add(options.RetentionPeriod),
                request.Definition.ConsentVersion);
            var payload = new FormDeliveryJobPayload(
                submission.Id,
                request.Definition.DefinitionVersionId);
            var job = await jobStore.EnqueueAsync(
                    new JobEnqueueRequest(
                        FormDeliveryJobHandler.DefinitionKey,
                        payload.Serialize(),
                        $"forms-delivery:{submission.Id:N}:{request.Definition.DefinitionVersionId:N}",
                        request.CorrelationId,
                        request.AcceptedAtUtc),
                    request.AcceptedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            if (job.IsFailure)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<FormReceipt>(
                    job.Error.Code == JobStoreErrorCode.DefinitionNotFound
                        ? "delivery_not_configured"
                        : "delivery_enqueue_failed",
                    "Form delivery is not configured.");
            }

            submission.AttachDeliveryJob(job.Success.JobInstanceId);
            var fields = request.Definition.Fields.ToDictionary(
                field => field.Key,
                StringComparer.Ordinal);
            var values = request.Values.Select(pair => new FormSubmissionValue(
                    submission.Id,
                    fields[pair.Key].Id,
                    pair.Key,
                    pair.Value,
                    fields[pair.Key].PrivacyClass,
                    request.AcceptedAtUtc))
                .ToArray();

            dbContext.Add(submission);
            dbContext.AddRange(values);
            auditAppender.Stage(
                new IdentityAuditDescriptor(
                    ActorId: null,
                    Roles: new HashSet<string>(StringComparer.Ordinal),
                    Action: SubmissionAcceptAction,
                    TargetType: "FormSubmission",
                    TargetId: submission.Id.ToString("N"),
                    CorrelationId: request.CorrelationId),
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "accepted",
                    ["definitionVersionId"] =
                        request.Definition.DefinitionVersionId.ToString("N"),
                    ["valueCount"] = values.Length.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<FormReceipt, FormError>(
                new FormReceipt(submission.Id, request.AcceptedAtUtc));
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private async Task<Result<FormReceipt, FormError>> ResolveDuplicateAsync(
        ValidatedFormSubmission request,
        CancellationToken cancellationToken)
    {
        var windowStart = GetWindowStart(request.AcceptedAtUtc, options.DuplicateWindow);
        var existing = await dbContext.Set<FormSubmission>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.DefinitionVersionId ==
                        request.Definition.DefinitionVersionId &&
                    candidate.DuplicateFingerprint == request.DuplicateFingerprint &&
                    candidate.DuplicateWindowStartUtc == windowStart,
                cancellationToken)
            .ConfigureAwait(false);
        return existing is null
            ? Failure<FormReceipt>(
                "forms_persistence_failure",
                "The form submission could not be persisted.")
            : DuplicateResult(existing, request.CanonicalPayloadHash);
    }

    private async Task<Result<FormReceipt, FormError>> AuditSubmissionFailureAsync(
        ValidatedFormSubmission request,
        string errorCode)
    {
        try
        {
            using var auditCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await auditWriter.AppendAsync(
                    new IdentityAuditDescriptor(
                        ActorId: null,
                        Roles: new HashSet<string>(StringComparer.Ordinal),
                        Action: SubmissionAcceptAction,
                        TargetType: "FormSubmission",
                        TargetId: null,
                        CorrelationId: request.CorrelationId),
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "failed",
                        ["errorCode"] = errorCode,
                    },
                    auditCancellation.Token)
                .ConfigureAwait(false);
            return Failure<FormReceipt>(
                errorCode,
                "The form submission could not be persisted.");
        }
        catch
        {
            return Failure<FormReceipt>(
                "forms_audit_unavailable",
                "The form submission could not be accepted.");
        }
    }

    private async Task<Result<T, FormError>> AuditStoreFailureAsync<T>(
        IdentityAuditDescriptor audit,
        string errorCode,
        string message)
    {
        try
        {
            using var auditCancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await auditWriter.AppendAsync(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "failed",
                        ["errorCode"] = errorCode,
                    },
                    auditCancellation.Token)
                .ConfigureAwait(false);
            return Failure<T>(errorCode, message);
        }
        catch
        {
            return Failure<T>(
                "forms_audit_unavailable",
                "The Forms audit store is unavailable.");
        }
    }

    private FormError? ApplyRetentionAction(
        FormSubmission submission,
        FormRetentionAction action)
    {
        switch (action)
        {
            case FormRetentionAction.MarkEligible:
                if (timeProvider.GetUtcNow() < submission.RetentionEligibleAtUtc)
                {
                    return new FormError(
                        "retention_not_due",
                        "The submission is not yet eligible for retention processing.");
                }

                submission.MarkRetentionEligible();
                return null;
            case FormRetentionAction.PlaceLegalHold:
                submission.PlaceLegalHold();
                return null;
            case FormRetentionAction.ReleaseLegalHold:
                submission.ReleaseLegalHold();
                return null;
            case FormRetentionAction.Anonymize:
                if (submission.HasLegalHold)
                {
                    return new FormError(
                        "legal_hold",
                        "A legal hold prevents submission anonymization.");
                }

                if (submission.RetentionStatus != FormRetentionStatus.Eligible)
                {
                    return new FormError(
                        "retention_not_eligible",
                        "The submission must be marked eligible before anonymization.");
                }

                if (timeProvider.GetUtcNow() < submission.RetentionEligibleAtUtc)
                {
                    return new FormError(
                        "retention_not_due",
                        "The submission is not yet eligible for anonymization.");
                }

                submission.Anonymize(timeProvider.GetUtcNow());
                return null;
            default:
                return new FormError(
                    "invalid_retention_action",
                    "The retention action is invalid.");
        }
    }

    private async Task<FormDefinitionView?> LoadActiveDefinitionAsync(
        string formKey,
        CancellationToken cancellationToken)
    {
        var definition = await dbContext.Set<FormDefinition>()
            .AsNoTracking()
            .Select(candidate => new
            {
                Entity = candidate,
                StateRowVersion = EF.Property<byte[]>(
                    candidate,
                    PersistencePropertyNames.RowVersion),
            })
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Entity.Key == formKey &&
                    candidate.Entity.IsEnabled &&
                    candidate.Entity.PublishedVersionId != null,
                cancellationToken)
            .ConfigureAwait(false);
        if (definition?.Entity.PublishedVersionId is not { } versionId)
        {
            return null;
        }

        var version = await dbContext.Set<FormDefinitionVersion>()
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == versionId, cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        var fields = await dbContext.Set<FormField>()
            .AsNoTracking()
            .Where(candidate => candidate.DefinitionVersionId == versionId)
            .OrderBy(candidate => candidate.Order)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new FormDefinitionView(
            definition.Entity.Id,
            definition.Entity.Key,
            definition.Entity.Title,
            version.Id,
            version.Version,
            version.ConsentVersion,
            fields.Select(ToView).ToArray(),
            new RowVersion(definition.StateRowVersion));
    }

    private static FormFieldView ToView(FormField field) =>
        new(
            field.Id,
            field.DefinitionVersionId,
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
            field.PrivacyClass);

    private static Result<FormReceipt, FormError> DuplicateResult(
        FormSubmission existing,
        byte[] payloadHash) =>
        CryptographicOperations.FixedTimeEquals(
            existing.CanonicalPayloadHash,
            payloadHash)
            ? Result.Succeed<FormReceipt, FormError>(
                new FormReceipt(existing.Id, existing.AcceptedAtUtc))
            : Failure<FormReceipt>(
                "duplicate_conflict",
                "The duplicate request fingerprint is associated with different form data.");

    private static DateTimeOffset GetWindowStart(
        DateTimeOffset acceptedAtUtc,
        TimeSpan window)
    {
        var utcTicks = acceptedAtUtc.ToUniversalTime().Ticks;
        var bucketTicks = utcTicks - (utcTicks % window.Ticks);
        return new DateTimeOffset(bucketTicks, TimeSpan.Zero);
    }

    private static string CreateReasonFingerprint(string reason) =>
        "sha256:" + Convert.ToHexString(
            SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(reason.Trim().Normalize())));

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static bool IsDeadlock(DbUpdateException exception) =>
        IsSqlError(exception, 1205);

    private static bool IsCompletedTransaction(InvalidOperationException exception) =>
        exception.Message.Contains(
            "transaction has completed",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSqlError(Exception exception, int number)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && sqlException.Number == number)
            {
                return true;
            }
        }

        return false;
    }

    private static Result<T, FormError> Failure<T>(string code, string message) =>
        Result.Fail<T, FormError>(new FormError(code, message));
}

internal sealed class FormsAuditAppender(
    HusayniaDbContext dbContext,
    ISensitiveDataRedactor redactor,
    TimeProvider timeProvider)
{
    internal void Stage(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details)
    {
        var safeDetails = redactor.Redact(details);
        dbContext.Add(new AuditEvent(
            descriptor.ActorId,
            JsonSerializer.Serialize(
                descriptor.Roles.OrderBy(role => role, StringComparer.Ordinal)),
            descriptor.Action,
            descriptor.TargetType,
            descriptor.TargetId,
            outcome,
            descriptor.CorrelationId,
            timeProvider.GetUtcNow().ToUniversalTime(),
            JsonSerializer.Serialize(
                safeDetails.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal))));
    }
}
