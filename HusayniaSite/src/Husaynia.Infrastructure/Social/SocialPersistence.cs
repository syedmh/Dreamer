using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Social;

public sealed class SocialFeedSnapshotConfiguration
    : IEntityTypeConfiguration<PersistedSocialFeedSnapshot>
{
    public void Configure(EntityTypeBuilder<PersistedSocialFeedSnapshot> builder)
    {
        builder.ToTable("SocialFeedSnapshots");
        builder.HasKey(snapshot => snapshot.Id);
        builder.Property(snapshot => snapshot.Provider)
            .HasMaxLength(SocialFeedLimits.ProviderLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(snapshot => snapshot.Version).IsRequired();
        builder.Property(snapshot => snapshot.FetchedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(snapshot => snapshot.ExpiresAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(snapshot => new { snapshot.Provider, snapshot.Version }).IsUnique();
        builder.HasMany(snapshot => snapshot.Items)
            .WithOne()
            .HasForeignKey(item => item.SnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SocialFeedItemConfiguration
    : IEntityTypeConfiguration<PersistedSocialFeedItem>
{
    public void Configure(EntityTypeBuilder<PersistedSocialFeedItem> builder)
    {
        builder.ToTable("SocialFeedSnapshotItems");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ExternalId)
            .HasMaxLength(SocialFeedLimits.ExternalIdLength)
            .IsRequired();
        builder.Property(item => item.Text)
            .HasMaxLength(SocialFeedLimits.TextLength)
            .IsRequired();
        builder.Property(item => item.SourceLink)
            .HasMaxLength(SocialFeedLimits.LinkLength);
        builder.Property(item => item.MediaType)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);
        builder.Property(item => item.MediaUrl)
            .HasMaxLength(SocialFeedLimits.LinkLength);
        builder.Property(item => item.ThumbnailUrl)
            .HasMaxLength(SocialFeedLimits.LinkLength);
        builder.Property(item => item.AltText)
            .HasMaxLength(SocialFeedLimits.AltTextLength);
        builder.Property(item => item.Caption)
            .HasMaxLength(SocialFeedLimits.CaptionLength);
        builder.Property(item => item.PublishedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(item => item.Ordinal).IsRequired();
        builder.HasIndex(item => new { item.SnapshotId, item.ExternalId }).IsUnique();
        builder.HasIndex(item => new { item.SnapshotId, item.Ordinal }).IsUnique();
    }
}

public sealed class SocialIntegrationStateConfiguration
    : MutableEntityConfiguration<SocialIntegrationState>
{
    protected override void ConfigureMutableEntity(
        EntityTypeBuilder<SocialIntegrationState> builder)
    {
        builder.ToTable("SocialIntegrationStates");
        builder.HasKey(state => state.Provider);
        builder.Property(state => state.Provider)
            .HasMaxLength(SocialFeedLimits.ProviderLength)
            .IsUnicode(false);
        builder.Property(state => state.LastAttemptAtUtc).HasPrecision(7);
        builder.Property(state => state.LastSuccessAtUtc).HasPrecision(7);
        builder.Property(state => state.LastFailureAtUtc).HasPrecision(7);
        builder.Property(state => state.RetryAfterUtc).HasPrecision(7);
        builder.Property(state => state.LastError)
            .HasConversion<string>()
            .HasMaxLength(SocialFeedLimits.ErrorCodeLength)
            .IsUnicode(false);
    }
}

public sealed class EfSocialSnapshotStore(HusayniaDbContext dbContext)
    : ISocialSnapshotStore
{
    public async Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireProviderLockAsync(
            normalizedProvider,
            "Shared",
            cancellationToken).ConfigureAwait(false);
        var state = await dbContext.Set<SocialIntegrationState>()
            .AsNoTracking()
            .Where(entry => entry.Provider == normalizedProvider)
            .Select(entry => new
            {
                entry.ActiveSnapshotId,
                entry.LastAttemptAtUtc,
                entry.LastSuccessAtUtc,
                entry.LastFailureAtUtc,
                entry.LastError,
                entry.RetryAfterUtc,
                entry.ConsecutiveFailures,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var activeSnapshot = state?.ActiveSnapshotId is not Guid snapshotId
            ? null
            : await dbContext.Set<PersistedSocialFeedSnapshot>()
                .AsNoTracking()
                .Where(snapshot => snapshot.Id == snapshotId)
                .Select(snapshot => new SocialSnapshotSchedulingProjection(
                    snapshot.Version,
                    snapshot.ExpiresAtUtc))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        var refreshState = state is null
            ? null
            : new SocialRefreshState(
                state.LastAttemptAtUtc,
                state.LastSuccessAtUtc,
                state.LastFailureAtUtc,
                state.LastError,
                state.RetryAfterUtc,
                state.ConsecutiveFailures);
        var result = new SocialRefreshSchedulingState(
            activeSnapshot?.Version,
            activeSnapshot?.ExpiresAtUtc,
            refreshState);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<SocialRefreshState?> ReadRefreshStateAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        return await dbContext.Set<SocialIntegrationState>()
            .AsNoTracking()
            .Where(state => state.Provider == normalizedProvider)
            .Select(state => new SocialRefreshState(
                state.LastAttemptAtUtc,
                state.LastSuccessAtUtc,
                state.LastFailureAtUtc,
                state.LastError,
                state.RetryAfterUtc,
                state.ConsecutiveFailures))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredSocialFeed?> ReadAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireProviderLockAsync(
            normalizedProvider,
            "Shared",
            cancellationToken).ConfigureAwait(false);
        var state = await dbContext.Set<SocialIntegrationState>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entry => entry.Provider == normalizedProvider,
                cancellationToken)
            .ConfigureAwait(false);
        if (state?.ActiveSnapshotId is not Guid snapshotId)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var snapshot = await dbContext.Set<PersistedSocialFeedSnapshot>()
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Id == snapshotId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var items = await dbContext.Set<PersistedSocialFeedItem>()
            .AsNoTracking()
            .Where(item => item.SnapshotId == snapshotId)
            .OrderBy(item => item.Ordinal)
            .Select(item => new StoredSocialFeedItem(
                item.ExternalId,
                item.Text,
                item.SourceLink == null ? null : new Uri(item.SourceLink, UriKind.Absolute),
                item.MediaType,
                item.MediaUrl == null ? null : new Uri(item.MediaUrl, UriKind.Absolute),
                item.ThumbnailUrl == null ? null : new Uri(item.ThumbnailUrl, UriKind.Absolute),
                item.Width,
                item.Height,
                item.DurationSeconds,
                item.AltText,
                item.Caption,
                item.PublishedAtUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var result = new StoredSocialFeed(
            normalizedProvider,
            snapshot.Version,
            snapshot.FetchedAtUtc,
            snapshot.ExpiresAtUtc,
            state.LastSuccessAtUtc,
            state.LastFailureAtUtc,
            items);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<long> ReplaceAsync(
        NormalizedSocialFeed feed,
        int retainedVersions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedVersions, 1);
        var provider = SocialFeedValidation.NormalizeProvider(feed.Provider);

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireProviderLockAsync(provider, "Exclusive", cancellationToken).ConfigureAwait(false);

        var currentVersion = await dbContext.Set<PersistedSocialFeedSnapshot>()
            .Where(snapshot => snapshot.Provider == provider)
            .MaxAsync(snapshot => (long?)snapshot.Version, cancellationToken)
            .ConfigureAwait(false) ?? 0;
        var nextVersion = checked(currentVersion + 1);
        var state = await dbContext.Set<SocialIntegrationState>()
            .SingleOrDefaultAsync(entry => entry.Provider == provider, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new SocialIntegrationState(provider);
            dbContext.Add(state);
        }

        var snapshot = new PersistedSocialFeedSnapshot(
            Guid.NewGuid(),
            provider,
            nextVersion,
            feed.FetchedAtUtc,
            feed.ExpiresAtUtc);
        var ordinal = 0;
        foreach (var item in feed.Items)
        {
            snapshot.Items.Add(new PersistedSocialFeedItem(
                snapshot.Id,
                item.ExternalId,
                item.Text,
                item.SourceLink,
                item.MediaType,
                item.MediaUrl,
                item.ThumbnailUrl,
                item.Width,
                item.Height,
                item.DurationSeconds,
                item.AltText,
                item.Caption,
                item.PublishedAtUtc,
                ordinal++));
        }

        if (!state.TryRecordSuccess(snapshot.Id, feed.RefreshedAtUtc) &&
            state.ActiveSnapshotId is Guid activeSnapshotId)
        {
            var activeVersion = await dbContext.Set<PersistedSocialFeedSnapshot>()
                .Where(entry => entry.Id == activeSnapshotId)
                .Select(entry => (long?)entry.Version)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (activeVersion.HasValue)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return activeVersion.Value;
            }
        }

        dbContext.Add(snapshot);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var expiredSnapshots = await dbContext.Set<PersistedSocialFeedSnapshot>()
            .Where(entry => entry.Provider == provider)
            .OrderByDescending(entry => entry.Version)
            .Skip(retainedVersions)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (expiredSnapshots.Length > 0)
        {
            dbContext.RemoveRange(expiredSnapshots);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return nextVersion;
    }

    public async Task RecordFailureAsync(
        string provider,
        SocialRefreshError failure,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset? retryAfterUtc,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireProviderLockAsync(
            normalizedProvider,
            "Exclusive",
            cancellationToken).ConfigureAwait(false);
        var state = await dbContext.Set<SocialIntegrationState>()
            .SingleOrDefaultAsync(
                entry => entry.Provider == normalizedProvider,
                cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new SocialIntegrationState(normalizedProvider);
            dbContext.Add(state);
        }

        if (state.TryRecordFailure(failure, occurredAtUtc, retryAfterUtc))
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeferUntilAsync(
        string provider,
        SocialRefreshError failure,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset retryAfterUtc,
        CancellationToken cancellationToken)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await AcquireProviderLockAsync(
            normalizedProvider,
            "Exclusive",
            cancellationToken).ConfigureAwait(false);
        var state = await dbContext.Set<SocialIntegrationState>()
            .SingleOrDefaultAsync(
                entry => entry.Provider == normalizedProvider,
                cancellationToken)
            .ConfigureAwait(false);
        if (state?.TryDeferUntil(
                failure,
                occurredAtUtc,
                retryAfterUtc) == true)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<int> AcquireProviderLockAsync(
        string provider,
        string lockMode,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DECLARE @result int;
             EXEC @result = sys.sp_getapplock
                 @Resource = {"Husaynia:Social:" + provider},
                 @LockMode = {lockMode},
                 @LockOwner = 'Transaction',
                 @LockTimeout = 10000;
             IF @result < 0 THROW 51000, 'Unable to acquire the social refresh lock.', 1;
             """,
            cancellationToken);

    private sealed record SocialSnapshotSchedulingProjection(
        long Version,
        DateTimeOffset ExpiresAtUtc);
}
