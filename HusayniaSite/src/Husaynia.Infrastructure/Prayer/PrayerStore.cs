using System.Data;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Identity;
using Husaynia.Domain.Prayer;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Prayer;

public sealed class EfPrayerStore(
    HusayniaDbContext dbContext,
    PrayerTransactionalAuditAppender auditAppender,
    IAuditWriter auditWriter,
    TimeProvider timeProvider) :
    IPrayerScheduleStore,
    IPrayerAdministrationStore,
    IPrayerProfileDefinitionReader,
    IPrayerSchedulingStore,
    IPrayerRefreshStore
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly PrayerTransactionalAuditAppender auditAppender =
        auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
    private readonly IAuditWriter auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<PrayerMonthData?> ReadMonthAsync(
        YearMonth month,
        CancellationToken cancellationToken)
    {
        var profileHash = await dbContext.Set<PrayerIntegrationState>()
            .AsNoTracking()
            .Where(state => state.Id == PrayerIntegrationState.PrimaryId)
            .Select(state => state.ActiveProfileHash)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (profileHash is null)
        {
            return null;
        }

        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var snapshots = await dbContext.Set<PrayerSnapshot>()
            .AsNoTracking()
            .Where(snapshot =>
                snapshot.ProfileHash == profileHash &&
                snapshot.Date >= first &&
                snapshot.Date <= last)
            .OrderBy(snapshot => snapshot.Date)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var overrides = await dbContext.Set<PrayerOverride>()
            .AsNoTracking()
            .Where(prayerOverride =>
                prayerOverride.ProfileHash == profileHash &&
                prayerOverride.Date >= first &&
                prayerOverride.Date <= last &&
                prayerOverride.IsActive)
            .OrderBy(prayerOverride => prayerOverride.Date)
            .ThenBy(prayerOverride => prayerOverride.PrayerKey)
            .ThenByDescending(prayerOverride => prayerOverride.EffectiveRevision)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PrayerMonthData(
            profileHash,
            snapshots.Select(snapshot => new PrayerSnapshotData(
                snapshot.Date,
                CanonicalPrayerValues.Parse(snapshot.ValuesJson),
                snapshot.GeneratedAtUtc,
                snapshot.Source,
                snapshot.IsValid)).ToArray(),
            overrides.Select(prayerOverride => new PrayerOverrideData(
                prayerOverride.Id,
                prayerOverride.Date,
                prayerOverride.PrayerKey,
                prayerOverride.LocalTime,
                prayerOverride.EffectiveRevision)).ToArray());
    }

    public async Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
        string profileHash,
        CancellationToken cancellationToken)
    {
        var definition = await dbContext.Set<PrayerProfile>()
            .AsNoTracking()
            .Where(profile => profile.ProfileHash == profileHash)
            .Select(profile => new PrayerProfileDefinition(
                profile.ProfileHash,
                profile.ProviderKind,
                profile.Latitude,
                profile.Longitude,
                profile.MethodJson,
                profile.AlgorithmVersion,
                profile.TimeZoneId,
                profile.EffectiveFrom))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return definition is null
            ? Result.Fail<PrayerProfileDefinition, PrayerAdministrationError>(
                new("profile_not_found", "The prayer profile was not found."))
            : Result.Succeed<PrayerProfileDefinition, PrayerAdministrationError>(definition);
    }

    public async Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        var definition = await dbContext.Set<PrayerProfile>()
            .AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => new PrayerProfileDefinition(
                profile.ProfileHash,
                profile.ProviderKind,
                profile.Latitude,
                profile.Longitude,
                profile.MethodJson,
                profile.AlgorithmVersion,
                profile.TimeZoneId,
                profile.EffectiveFrom))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return definition is null
            ? Result.Fail<PrayerProfileDefinition, PrayerAdministrationError>(
                new("profile_not_found", "The prayer profile was not found."))
            : Result.Succeed<PrayerProfileDefinition, PrayerAdministrationError>(definition);
    }

    public async Task<PrayerActiveProfile?> ReadActiveProfileAsync(
        CancellationToken cancellationToken) =>
        await (
            from state in dbContext.Set<PrayerIntegrationState>().AsNoTracking()
            join profile in dbContext.Set<PrayerProfile>().AsNoTracking()
                on state.ActiveProfileHash equals profile.ProfileHash
            where state.Id == PrayerIntegrationState.PrimaryId &&
                state.RefreshIntentProfileHash == state.ActiveProfileHash
            select new PrayerActiveProfile(profile.ProfileHash, profile.EffectiveFrom))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<Result<PrayerProfileView, PrayerAdministrationError>> CreateProfileAsync(
        PrayerProfile profile,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(audit);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existing = await dbContext.Set<PrayerProfile>()
                .SingleOrDefaultAsync(
                    candidate => candidate.ProfileHash == profile.ProfileHash,
                    cancellationToken)
                .ConfigureAwait(false);
            var persisted = existing ?? profile;
            if (existing is null)
            {
                dbContext.Add(profile);
            }

            var state = await dbContext.Set<PrayerIntegrationState>()
                .AsNoTracking()
                .Where(candidate => candidate.Id == PrayerIntegrationState.PrimaryId)
                .Select(candidate => new
                {
                    candidate.ActiveProfileHash,
                    RowVersion = EF.Property<byte[]>(
                        candidate,
                        PersistencePropertyNames.RowVersion),
                })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var view = ToProfileView(
                persisted,
                string.Equals(
                    state?.ActiveProfileHash,
                    persisted.ProfileHash,
                    StringComparison.Ordinal),
                new RowVersion(state?.RowVersion ?? []),
                string.Equals(
                    state?.ActiveProfileHash,
                    persisted.ProfileHash,
                    StringComparison.Ordinal)
                    ? PrayerRefreshSchedulingStatuses.Pending
                    : PrayerRefreshSchedulingStatuses.NotRequested);
            auditAppender.Append(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                Details(existing is null ? "created" : "existing"));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                view);
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            var code = IsUniqueViolation(exception)
                ? "profile_conflict"
                : "persistence_failure";
            return await ProfileFailureAsync(audit, code).ConfigureAwait(false);
        }
    }

    public async Task<Result<PrayerProfileView, PrayerAdministrationError>> ActivateProfileAsync(
        ActivatePrayerProfileCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var profile = await dbContext.Set<PrayerProfile>()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == command.ProfileId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                await CommitStoreOutcomeAsync(
                        transaction,
                        audit,
                        "not_found",
                        "profile_not_found",
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Fail<PrayerProfileView, PrayerAdministrationError>(
                    new("profile_not_found", "The prayer profile was not found."));
            }

            var state = await dbContext.Set<PrayerIntegrationState>()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == PrayerIntegrationState.PrimaryId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (state is null)
            {
                if (!command.ExpectedStateRowVersion.Value.IsEmpty)
                {
                    await CommitStoreOutcomeAsync(
                            transaction,
                            audit,
                            "conflict",
                            "concurrency_conflict",
                            cancellationToken)
                        .ConfigureAwait(false);
                    return Conflict<PrayerProfileView>();
                }

                state = new PrayerIntegrationState();
                dbContext.Add(state);
            }
            else if (!MatchesRowVersion(state, command.ExpectedStateRowVersion))
            {
                await CommitStoreOutcomeAsync(
                        transaction,
                        audit,
                        "conflict",
                        "concurrency_conflict",
                        cancellationToken)
                    .ConfigureAwait(false);
                return Conflict<PrayerProfileView>();
            }

            state.Activate(profile.ProfileHash, timeProvider.GetUtcNow());
            auditAppender.Append(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                Details("activated"));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                ToProfileView(
                    profile,
                    true,
                    RowVersionOf(state),
                    PrayerRefreshSchedulingStatuses.Pending));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return await ProfileFailureAsync(audit, "concurrency_conflict").ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return await ProfileFailureAsync(audit, "persistence_failure").ConfigureAwait(false);
        }
    }

    public async Task<Result<PrayerOverrideView, PrayerAdministrationError>> SaveOverrideAsync(
        SavePrayerOverrideCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var profileExists = await dbContext.Set<PrayerProfile>()
                .AnyAsync(
                    profile => profile.ProfileHash == command.ProfileHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!profileExists)
            {
                await CommitStoreOutcomeAsync(
                        transaction,
                        audit,
                        "not_found",
                        "profile_not_found",
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Fail<PrayerOverrideView, PrayerAdministrationError>(
                    new("profile_not_found", "The prayer profile was not found."));
            }

            var activeProfileHash = await dbContext.Set<PrayerIntegrationState>()
                .Where(state => state.Id == PrayerIntegrationState.PrimaryId)
                .Select(state => state.ActiveProfileHash)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    activeProfileHash,
                    command.ProfileHash,
                    StringComparison.Ordinal))
            {
                await CommitStoreOutcomeAsync(
                        transaction,
                        audit,
                        "conflict",
                        "profile_not_active",
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Fail<PrayerOverrideView, PrayerAdministrationError>(
                    new(
                        "profile_not_active",
                        "Overrides may be saved only for the active prayer profile."));
            }

            var revision = 1 + (await dbContext.Set<PrayerOverride>()
                .Where(prayerOverride =>
                    prayerOverride.ProfileHash == command.ProfileHash &&
                    prayerOverride.Date == command.Date &&
                    prayerOverride.PrayerKey == command.PrayerKey)
                .MaxAsync(
                    prayerOverride => (long?)prayerOverride.EffectiveRevision,
                    cancellationToken)
                .ConfigureAwait(false) ?? 0);
            var prayerOverride = PrayerOverride.Create(
                command.ProfileHash,
                command.Date,
                command.PrayerKey,
                command.LocalTime,
                command.Reason,
                revision,
                audit.ActorId ?? "unknown");
            dbContext.Add(prayerOverride);
            auditAppender.Append(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                Details("saved"));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<PrayerOverrideView, PrayerAdministrationError>(
                ToOverrideView(prayerOverride));
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            var code = IsUniqueViolation(exception)
                ? "override_conflict"
                : "persistence_failure";
            return await OverrideFailureAsync(audit, code).ConfigureAwait(false);
        }
    }

    public async Task<Result<PrayerOverrideView, PrayerAdministrationError>> DeactivateOverrideAsync(
        DeactivatePrayerOverrideCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var prayerOverride = await dbContext.Set<PrayerOverride>()
                .SingleOrDefaultAsync(
                    candidate => candidate.Id == command.OverrideId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (prayerOverride is null)
            {
                await CommitStoreOutcomeAsync(
                        transaction,
                        audit,
                        "not_found",
                        "override_not_found",
                        cancellationToken)
                    .ConfigureAwait(false);
                return Result.Fail<PrayerOverrideView, PrayerAdministrationError>(
                    new("override_not_found", "The prayer override was not found."));
            }

            if (!MatchesRowVersion(prayerOverride, command.ExpectedRowVersion))
            {
                await CommitStoreOutcomeAsync(
                        transaction,
                        audit,
                        "conflict",
                        "concurrency_conflict",
                        cancellationToken)
                    .ConfigureAwait(false);
                return Conflict<PrayerOverrideView>();
            }

            prayerOverride.Deactivate(
                audit.ActorId ?? "unknown",
                timeProvider.GetUtcNow());
            auditAppender.Append(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                Details("deactivated"));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<PrayerOverrideView, PrayerAdministrationError>(
                ToOverrideView(prayerOverride));
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return await OverrideFailureAsync(audit, "concurrency_conflict").ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return await OverrideFailureAsync(audit, "persistence_failure").ConfigureAwait(false);
        }
    }

    public async Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> ReplaceMonthAsync(
        PrayerSnapshotBatch batch,
        string source,
        IdentityAuditDescriptor? audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var profileExists = await dbContext.Set<PrayerProfile>()
                .AnyAsync(
                    profile => profile.ProfileHash == batch.ProfileHash,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!profileExists)
            {
                return await RefreshStoreFailureAsync(
                        transaction,
                        audit,
                        new("profile_not_found", "The prayer profile was not found."),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var first = new DateOnly(batch.Year, batch.Month, 1);
            var last = first.AddMonths(1).AddDays(-1);
            var existing = await dbContext.Set<PrayerSnapshot>()
                .Where(snapshot =>
                    snapshot.ProfileHash == batch.ProfileHash &&
                    snapshot.Date >= first &&
                    snapshot.Date <= last)
                .ToDictionaryAsync(snapshot => snapshot.Date, cancellationToken)
                .ConfigureAwait(false);
            foreach (var snapshot in batch.Snapshots)
            {
                if (existing.TryGetValue(snapshot.Date, out var current))
                {
                    current.Replace(
                        snapshot.ValuesJson,
                        snapshot.GeneratedAtUtc,
                        source,
                        true);
                }
                else
                {
                    dbContext.Add(snapshot);
                }
            }

            var state = await GetOrCreateStateAsync(cancellationToken).ConfigureAwait(false);
            state.RecordSuccess(
                timeProvider.GetUtcNow(),
                source,
                first);
            if (audit is not null)
            {
                auditAppender.Append(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    Details("refreshed"));
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<PrayerRefreshReceipt, PrayerRefreshError>(
                new(
                    batch.ProfileHash,
                    new YearMonth(batch.Year, batch.Month),
                    batch.Snapshots.Count));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return await RefreshFailureAsync(
                    audit,
                    "persistence_failure",
                    "The prayer snapshot could not be persisted.")
                .ConfigureAwait(false);
        }
    }

    public async Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RecordRefreshFailureAsync(
        string profileHash,
        YearMonth month,
        PrayerRefreshError refreshFailure,
        IdentityAuditDescriptor? audit,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var state = await GetOrCreateStateAsync(cancellationToken).ConfigureAwait(false);
            state.RecordFailure(timeProvider.GetUtcNow(), refreshFailure.Code);
            if (audit is not null)
            {
                auditAppender.Append(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    Details("dependency_failed", refreshFailure.Code));
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<PrayerRefreshReceipt, PrayerRefreshError>(refreshFailure);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return await RefreshFailureAsync(
                    audit,
                    "persistence_failure",
                    "The prayer failure state could not be persisted.")
                .ConfigureAwait(false);
        }
    }

    private static PrayerProfileView ToProfileView(
        PrayerProfile profile,
        bool isActive,
        RowVersion stateRowVersion,
        string refreshSchedulingStatus) =>
        new(
            profile.Id,
            profile.ProfileHash,
            profile.ProviderKind,
            profile.Latitude,
            profile.Longitude,
            profile.MethodJson,
            profile.AlgorithmVersion,
            profile.TimeZoneId,
            profile.EffectiveFrom,
            isActive,
            stateRowVersion,
            refreshSchedulingStatus);

    private PrayerOverrideView ToOverrideView(PrayerOverride prayerOverride) =>
        new(
            prayerOverride.Id,
            prayerOverride.ProfileHash,
            prayerOverride.Date,
            prayerOverride.PrayerKey,
            prayerOverride.LocalTime,
            prayerOverride.Reason,
            prayerOverride.EffectiveRevision,
            prayerOverride.IsActive,
            RowVersionOf(prayerOverride));

    private async Task<PrayerIntegrationState> GetOrCreateStateAsync(
        CancellationToken cancellationToken)
    {
        var state = await dbContext.Set<PrayerIntegrationState>()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == PrayerIntegrationState.PrimaryId,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new PrayerIntegrationState();
            dbContext.Add(state);
        }

        return state;
    }

    private bool MatchesRowVersion<TEntity>(TEntity entity, RowVersion expected)
        where TEntity : class =>
        expected.Value.Span.SequenceEqual(
            dbContext.Entry(entity)
                .Property<byte[]>(PersistencePropertyNames.RowVersion)
                .CurrentValue);

    private RowVersion RowVersionOf<TEntity>(TEntity entity)
        where TEntity : class =>
        new(
            dbContext.Entry(entity)
                .Property<byte[]>(PersistencePropertyNames.RowVersion)
                .CurrentValue);

    private async Task CommitStoreOutcomeAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        IdentityAuditDescriptor audit,
        string result,
        string errorCode,
        CancellationToken cancellationToken)
    {
        auditAppender.Append(
            audit,
            PrivilegedAttemptOutcome.Allowed,
            Details(result, errorCode));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RefreshStoreFailureAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        IdentityAuditDescriptor? audit,
        PrayerRefreshError error,
        CancellationToken cancellationToken)
    {
        if (audit is not null)
        {
            auditAppender.Append(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                Details("not_found", error.Code));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Fail<PrayerRefreshReceipt, PrayerRefreshError>(error);
    }

    private async Task<Result<PrayerProfileView, PrayerAdministrationError>> ProfileFailureAsync(
        IdentityAuditDescriptor audit,
        string code)
    {
        if (!await AppendFailureAuditAsync(audit, code).ConfigureAwait(false))
        {
            return Result.Fail<PrayerProfileView, PrayerAdministrationError>(
                AuditUnavailable());
        }

        return code == "concurrency_conflict"
            ? Conflict<PrayerProfileView>()
            : Result.Fail<PrayerProfileView, PrayerAdministrationError>(
                new(code, "The prayer profile operation could not be persisted."));
    }

    private async Task<Result<PrayerOverrideView, PrayerAdministrationError>> OverrideFailureAsync(
        IdentityAuditDescriptor audit,
        string code)
    {
        if (!await AppendFailureAuditAsync(audit, code).ConfigureAwait(false))
        {
            return Result.Fail<PrayerOverrideView, PrayerAdministrationError>(
                AuditUnavailable());
        }

        return code == "concurrency_conflict"
            ? Conflict<PrayerOverrideView>()
            : Result.Fail<PrayerOverrideView, PrayerAdministrationError>(
                new(code, "The prayer override operation could not be persisted."));
    }

    private async Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RefreshFailureAsync(
        IdentityAuditDescriptor? audit,
        string code,
        string message)
    {
        if (audit is not null &&
            !await AppendFailureAuditAsync(audit, code).ConfigureAwait(false))
        {
            return Result.Fail<PrayerRefreshReceipt, PrayerRefreshError>(
                new("prayer_audit_unavailable", "The prayer audit outcome could not be persisted."));
        }

        return Result.Fail<PrayerRefreshReceipt, PrayerRefreshError>(
            new(code, message));
    }

    private async Task<bool> AppendFailureAuditAsync(
        IdentityAuditDescriptor audit,
        string code)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await auditWriter.AppendAsync(
                    audit,
                    PrivilegedAttemptOutcome.Allowed,
                    Details("failed", code),
                    cancellation.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PrayerAdministrationError AuditUnavailable() =>
        new(
            "prayer_audit_unavailable",
            "The prayer audit outcome could not be persisted.");

    private static Result<T, PrayerAdministrationError> Conflict<T>() =>
        Result.Fail<T, PrayerAdministrationError>(
            new(
                "concurrency_conflict",
                "The prayer record changed; refresh and retry."));

    private static Dictionary<string, string?> Details(
        string result,
        string? errorCode = null)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["result"] = result,
        };
        if (errorCode is not null)
        {
            details["errorCode"] = errorCode;
        }

        return details;
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
