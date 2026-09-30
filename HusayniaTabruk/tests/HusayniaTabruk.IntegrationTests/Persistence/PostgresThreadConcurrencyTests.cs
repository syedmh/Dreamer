using System.Data.Common;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresThreadConcurrencyTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task PostCompareAndSwapInsideUnitOfWorkRejectsStaleWriterAndRollsBackAllLoserEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using StaleThreadContexts stale = await LoadStaleThreadsAsync(database, seed);
        LoadedDateThread first = stale.First;
        LoadedDateThread second = stale.Second;
        (var manager, _, var date) = seed.CreateThreadDomainView();

        Result<MessagePosted> firstPost = first.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "First concurrent post.",
            manager,
            date,
            primaryContactSignup: null,
            seed.Now);
        Assert.True(firstPost.IsSuccess, firstPost.IsFailure ? firstPost.Error.Message : null);
        Result firstSaved = await stale.FirstRepository.SaveAsync(first, seed.Effects("post"));
        Assert.True(firstSaved.IsSuccess, firstSaved.IsFailure ? firstSaved.Error.Message : null);

        Result<MessagePosted> secondPost = second.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Second stale post.",
            manager,
            date,
            primaryContactSignup: null,
            seed.Now);
        Assert.True(secondPost.IsSuccess, secondPost.IsFailure ? secondPost.Error.Message : null);
        Result secondSaved = await ExecuteWithinUnitOfWorkAsync(
            stale.SecondUnitOfWork,
            token => stale.SecondRepository.SaveAsync(second, seed.Effects("post"), token));

        await AssertThreadLoserRolledBackAsync(
            database,
            seed,
            expectedMessages: 1,
            expectedReports: 0,
            expectedModeration: 0,
            expectedVersion: 1,
            expectedStatus: ThreadStatus.Open,
            expectedLockedAt: null,
            expectedMessageVisibility: MessageVisibility.Visible);
        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);
    }

    [RequiresPostgresFact]
    public async Task ReportCompareAndSwapInsideUnitOfWorkRejectsStaleWriterAndRollsBackAllLoserEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MessageId messageId = await seed.AddVisibleThreadMessageAsync(database);
        await using StaleThreadContexts stale = await LoadStaleThreadsAsync(database, seed);
        LoadedDateThread first = stale.First;
        LoadedDateThread second = stale.Second;
        (var manager, _, var date) = seed.CreateThreadDomainView();

        Result<MessageReported> firstReport = first.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            "First report.",
            manager,
            date,
            primaryContactSignup: null,
            seed.Now);
        Assert.True(firstReport.IsSuccess, firstReport.IsFailure ? firstReport.Error.Message : null);
        Result firstSaved = await stale.FirstRepository.SaveAsync(first, seed.Effects("report"));
        Assert.True(firstSaved.IsSuccess, firstSaved.IsFailure ? firstSaved.Error.Message : null);

        Result<MessageReported> secondReport = second.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            "Stale report.",
            manager,
            date,
            primaryContactSignup: null,
            seed.Now);
        Assert.True(secondReport.IsSuccess, secondReport.IsFailure ? secondReport.Error.Message : null);
        Result secondSaved = await ExecuteWithinUnitOfWorkAsync(
            stale.SecondUnitOfWork,
            token => stale.SecondRepository.SaveAsync(second, seed.Effects("report"), token));

        await AssertThreadLoserRolledBackAsync(
            database,
            seed,
            expectedMessages: 1,
            expectedReports: 1,
            expectedModeration: 0,
            expectedVersion: 2,
            expectedStatus: ThreadStatus.Open,
            expectedLockedAt: null,
            expectedMessageVisibility: MessageVisibility.Visible);
        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);
    }

    [RequiresPostgresFact]
    public async Task HideCompareAndSwapInsideUnitOfWorkRejectsStaleWriterAndRollsBackAllLoserEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MessageId messageId = await seed.AddVisibleThreadMessageAsync(database);
        await using StaleThreadContexts stale = await LoadStaleThreadsAsync(database, seed);
        LoadedDateThread first = stale.First;
        LoadedDateThread second = stale.Second;
        (var manager, _, var date) = seed.CreateThreadDomainView();

        Result<MessageHidden> firstHidden = first.Thread.Hide(
            messageId,
            "First moderation action.",
            manager,
            date,
            seed.Now);
        Assert.True(firstHidden.IsSuccess, firstHidden.IsFailure ? firstHidden.Error.Message : null);
        Result firstSaved = await stale.FirstRepository.SaveAsync(first, seed.Effects("hide", messageId.Value));
        Assert.True(firstSaved.IsSuccess, firstSaved.IsFailure ? firstSaved.Error.Message : null);

        Result<MessageHidden> secondHidden = second.Thread.Hide(
            messageId,
            "Stale moderation action.",
            manager,
            date,
            seed.Now);
        Assert.True(secondHidden.IsSuccess, secondHidden.IsFailure ? secondHidden.Error.Message : null);
        Result secondSaved = await ExecuteWithinUnitOfWorkAsync(
            stale.SecondUnitOfWork,
            token => stale.SecondRepository.SaveAsync(second, seed.Effects("hide", messageId.Value), token));

        await AssertThreadLoserRolledBackAsync(
            database,
            seed,
            expectedMessages: 1,
            expectedReports: 0,
            expectedModeration: 1,
            expectedVersion: 2,
            expectedStatus: ThreadStatus.Open,
            expectedLockedAt: null,
            expectedMessageVisibility: MessageVisibility.Hidden);
        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);
        await using TabrukDbContext verification = database.CreateContext();
        ThreadMessageEntity message = await verification.ThreadMessages.SingleAsync(candidate => candidate.Id == messageId.Value);
        Assert.Equal(1, message.Visibility);
    }

    [RequiresPostgresFact]
    public async Task LockCompareAndSwapRejectsStaleWriterAndDuplicateAndConflictingRetriesWriteNothing()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using StaleThreadContexts stale = await LoadStaleThreadsAsync(database, seed);
        LoadedDateThread first = stale.First;
        LoadedDateThread second = stale.Second;
        (var manager, _, var date) = seed.CreateThreadDomainView();

        Result<ThreadLocked> firstLocked = first.Thread.Lock(
            "First lock.",
            manager,
            date,
            seed.Now);
        Assert.True(firstLocked.IsSuccess, firstLocked.IsFailure ? firstLocked.Error.Message : null);
        Result firstSaved = await stale.FirstRepository.SaveAsync(first, seed.Effects("lock"));
        Assert.True(firstSaved.IsSuccess, firstSaved.IsFailure ? firstSaved.Error.Message : null);

        Result<ThreadLocked> secondLocked = second.Thread.Lock(
            "Stale lock.",
            manager,
            date,
            seed.Now);
        Assert.True(secondLocked.IsSuccess, secondLocked.IsFailure ? secondLocked.Error.Message : null);
        Result secondSaved = await ExecuteWithinUnitOfWorkAsync(
            stale.SecondUnitOfWork,
            token => stale.SecondRepository.SaveAsync(second, seed.Effects("lock"), token));
        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);

        Result<LoadedDateThread> reloaded;
        await using (TabrukDbContext reloadContext = database.CreateContext())
        {
            reloaded = await new PostgresThreadRepository(reloadContext).GetAsync(
                seed.OrganizationId,
                seed.ThreadId);
        }

        Assert.True(reloaded.IsSuccess, reloaded.IsFailure ? reloaded.Error.Message : null);
        Assert.Equal(seed.Now, reloaded.Value.Thread.LockedAt);
        Assert.Equal(1, reloaded.Value.Thread.Version);
        Result<ThreadLocked> duplicate = reloaded.Value.Thread.Lock(
            "Different duplicate reason is not persisted.",
            manager,
            date,
            seed.Now);
        Assert.True(duplicate.IsSuccess, duplicate.IsFailure ? duplicate.Error.Message : null);
        Assert.True(duplicate.Value.WasDuplicate);
        await using (TabrukDbContext retryContext = database.CreateContext())
        {
            Result duplicateSaved = await new PostgresThreadRepository(retryContext).SaveAsync(
                reloaded.Value,
                ThreadPersistenceEffects.Empty);
            Assert.True(duplicateSaved.IsSuccess, duplicateSaved.IsFailure ? duplicateSaved.Error.Message : null);
        }

        Result<ThreadLocked> conflicting = reloaded.Value.Thread.Lock(
            "Conflicting retry.",
            manager,
            date,
            seed.Now.AddTicks(1));
        Assert.True(conflicting.IsFailure);
        Assert.Equal(ErrorCodes.InvalidTransition, conflicting.Error.Code);

        await AssertThreadLoserRolledBackAsync(
            database,
            seed,
            expectedMessages: 0,
            expectedReports: 0,
            expectedModeration: 1,
            expectedVersion: 1,
            expectedStatus: ThreadStatus.Locked,
            expectedLockedAt: seed.Now,
            expectedMessageVisibility: null);
    }

    [RequiresPostgresFact]
    public async Task AuthoritativeLockAndHiddenMessagePreventAllSubsequentForbiddenThreadWrites()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MessageId messageId = await seed.AddVisibleThreadMessageAsync(database);
        (var manager, _, var date) = seed.CreateThreadDomainView();

        await using (TabrukDbContext context = database.CreateContext())
        {
            PostgresThreadRepository repository = new(context);
            Result<LoadedDateThread> loaded = await repository.GetAsync(
                seed.OrganizationId,
                seed.ThreadId);
            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
            Result<MessageHidden> hidden = loaded.Value.Thread.Hide(
                messageId,
                "Hide before lock.",
                manager,
                date,
                seed.Now);
            Assert.True(hidden.IsSuccess, hidden.IsFailure ? hidden.Error.Message : null);
            Result saved = await repository.SaveAsync(loaded.Value, seed.Effects("hide", messageId.Value));
            Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        }

        await using TabrukDbContext lockedContext = database.CreateContext();
        PostgresThreadRepository lockedRepository = new(lockedContext);
        Result<LoadedDateThread> hiddenLoaded = await lockedRepository.GetAsync(
            seed.OrganizationId,
            seed.ThreadId);
        Assert.True(hiddenLoaded.IsSuccess, hiddenLoaded.IsFailure ? hiddenLoaded.Error.Message : null);
        Result<MessageReported> hiddenReport = hiddenLoaded.Value.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            null,
            manager,
            date,
            primaryContactSignup: null,
            seed.Now.AddTicks(1));
        Assert.True(hiddenReport.IsFailure);

        Result<ThreadLocked> locked = hiddenLoaded.Value.Thread.Lock(
            "Lock after hide.",
            manager,
            date,
            seed.Now.AddTicks(1));
        Assert.True(locked.IsSuccess, locked.IsFailure ? locked.Error.Message : null);
        Result lockSaved = await lockedRepository.SaveAsync(hiddenLoaded.Value, seed.Effects("lock"));
        Assert.True(lockSaved.IsSuccess, lockSaved.IsFailure ? lockSaved.Error.Message : null);

        await using TabrukDbContext afterLockContext = database.CreateContext();
        Result<LoadedDateThread> afterLock =
            await new PostgresThreadRepository(afterLockContext).GetAsync(
                seed.OrganizationId,
                seed.ThreadId);
        Assert.True(afterLock.IsSuccess, afterLock.IsFailure ? afterLock.Error.Message : null);
        Assert.True(afterLock.Value.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Post-lock message.",
            manager,
            date,
            primaryContactSignup: null,
            seed.Now.AddTicks(2)).IsFailure);
        Assert.True(afterLock.Value.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            null,
            manager,
            date,
            primaryContactSignup: null,
            seed.Now.AddTicks(2)).IsFailure);
        Assert.True(afterLock.Value.Thread.Hide(
            messageId,
            "Post-lock hide.",
            manager,
            date,
            seed.Now.AddTicks(2)).IsFailure);
    }

    [RequiresPostgresFact]
    public async Task RehydratedThreadVersionEqualsPersistedMessageReportHiddenAndLockEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        (var manager, _, var date) = seed.CreateThreadDomainView();
        MessageId messageId = MessageId.New();

        await using (TabrukDbContext postContext = database.CreateContext())
        {
            PostgresThreadRepository repository = new(postContext);
            Result<LoadedDateThread> loaded = await repository.GetAsync(seed.OrganizationId, seed.ThreadId);
            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
            Assert.True(
                loaded.Value.Thread.Post(
                    messageId,
                    IdempotencyKey.New(),
                    "Version formula coverage message.",
                    manager,
                    date,
                    primaryContactSignup: null,
                    seed.Now).IsSuccess);
            Result saved = await repository.SaveAsync(loaded.Value, ThreadPersistenceEffects.Empty);
            Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        }

        await using (TabrukDbContext reportContext = database.CreateContext())
        {
            PostgresThreadRepository repository = new(reportContext);
            Result<LoadedDateThread> loaded = await repository.GetAsync(seed.OrganizationId, seed.ThreadId);
            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
            Assert.True(
                loaded.Value.Thread.Report(
                    messageId,
                    MessageReportReason.Spam,
                    "Version formula coverage report.",
                    manager,
                    date,
                    primaryContactSignup: null,
                    seed.Now.AddTicks(1)).IsSuccess);
            Result saved = await repository.SaveAsync(loaded.Value, ThreadPersistenceEffects.Empty);
            Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        }

        await using (TabrukDbContext hideContext = database.CreateContext())
        {
            PostgresThreadRepository repository = new(hideContext);
            Result<LoadedDateThread> loaded = await repository.GetAsync(seed.OrganizationId, seed.ThreadId);
            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
            Assert.True(
                loaded.Value.Thread.Hide(
                    messageId,
                    "Version formula coverage hide.",
                    manager,
                    date,
                    seed.Now.AddTicks(2)).IsSuccess);
            Result saved = await repository.SaveAsync(loaded.Value, ThreadPersistenceEffects.Empty);
            Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        }

        await using (TabrukDbContext lockContext = database.CreateContext())
        {
            PostgresThreadRepository repository = new(lockContext);
            Result<LoadedDateThread> loaded = await repository.GetAsync(seed.OrganizationId, seed.ThreadId);
            Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
            Assert.True(
                loaded.Value.Thread.Lock(
                    "Version formula coverage lock.",
                    manager,
                    date,
                    seed.Now.AddTicks(3)).IsSuccess);
            Result saved = await repository.SaveAsync(loaded.Value, ThreadPersistenceEffects.Empty);
            Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
        }

        await using TabrukDbContext rehydrateContext = database.CreateContext();
        Result<LoadedDateThread> rehydrated =
            await new PostgresThreadRepository(rehydrateContext).GetAsync(seed.OrganizationId, seed.ThreadId);

        Assert.True(rehydrated.IsSuccess, rehydrated.IsFailure ? rehydrated.Error.Message : null);
        long expectedVersion = rehydrated.Value.Thread.Messages.Count
            + rehydrated.Value.Thread.Reports.Count
            + rehydrated.Value.Thread.Messages.Count(
                message => message.Visibility == MessageVisibility.Hidden)
            + (rehydrated.Value.Thread.LockedAt.HasValue ? 1L : 0L);
        Assert.Equal(4, expectedVersion);
        Assert.Equal(expectedVersion, rehydrated.Value.Thread.Version);
        Assert.Equal(expectedVersion, rehydrated.Value.LoadedVersion);
    }

    [RequiresPostgresFact]
    public async Task GetAsyncUsesOneSnapshotWhenAConcurrentPostCommitsAfterItsRootRead()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        (Membership manager, _, ServiceDate date) = seed.CreateThreadDomainView();
        ConcurrentPostCommitAfterThreadRootReadInterceptor interceptor = new(
            cancellationToken => CommitConcurrentPostAsync(
                database,
                seed,
                manager,
                date,
                cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<LoadedDateThread> loaded = await new PostgresThreadRepository(readerContext).GetAsync(
            seed.OrganizationId,
            seed.ThreadId);

        Assert.True(interceptor.ConcurrentPostCommitted);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        Assert.Empty(loaded.Value.Thread.Messages);
        Assert.Empty(loaded.Value.Thread.Reports);
        Assert.Equal(0, loaded.Value.Thread.Version);
        Assert.Equal(0, loaded.Value.LoadedVersion);

        await using TabrukDbContext verificationContext = database.CreateContext();
        DateThreadEntity committedThread = await verificationContext.DateThreads.SingleAsync(
            candidate => candidate.Id == seed.ThreadId.Value);
        Assert.Equal(1, committedThread.Version);
        Assert.Equal(
            1,
            await verificationContext.ThreadMessages.CountAsync(
                candidate => candidate.ThreadId == seed.ThreadId.Value));
    }

    [RequiresPostgresFact]
    public async Task GetAsyncRetriesToCoherentVersionWhenAnAmbientUnitOfWorkIsReadCommitted()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        (Membership manager, _, ServiceDate date) = seed.CreateThreadDomainView();
        ConcurrentPostCommitAfterThreadRootReadInterceptor interceptor = new(
            cancellationToken => CommitConcurrentPostAsync(
                database,
                seed,
                manager,
                date,
                cancellationToken));
        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using TabrukDbContext readerContext = new(options);
        Result<LoadedDateThread> loaded = await new PostgresUnitOfWork(readerContext).ExecuteAsync(
            cancellationToken => new PostgresThreadRepository(readerContext).GetAsync(
                seed.OrganizationId,
                seed.ThreadId,
                cancellationToken));

        Assert.True(interceptor.ConcurrentPostCommitted);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        Assert.Single(loaded.Value.Thread.Messages);
        Assert.Empty(loaded.Value.Thread.Reports);
        Assert.Equal(1, loaded.Value.Thread.Version);
        Assert.Equal(1, loaded.Value.LoadedVersion);
    }

    private static async Task<StaleThreadContexts> LoadStaleThreadsAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        TabrukDbContext firstContext = database.CreateContext();
        TabrukDbContext secondContext = database.CreateContext();
        PostgresThreadRepository firstRepository = new(firstContext);
        PostgresThreadRepository secondRepository = new(secondContext);
        Result<LoadedDateThread> first = await firstRepository.GetAsync(
            seed.OrganizationId,
            seed.ThreadId);
        Result<LoadedDateThread> second = await secondRepository.GetAsync(
            seed.OrganizationId,
            seed.ThreadId);
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);
        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Message : null);
        return new StaleThreadContexts(
            first.Value,
            second.Value,
            firstRepository,
            secondRepository,
            firstContext,
            secondContext);
    }

    private static async Task AssertThreadLoserRolledBackAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        int expectedMessages,
        int expectedReports,
        int expectedModeration,
        long expectedVersion,
        ThreadStatus expectedStatus,
        DateTimeOffset? expectedLockedAt,
        MessageVisibility? expectedMessageVisibility)
    {
        await using TabrukDbContext verification = database.CreateContext();
        DateThreadEntity thread = await verification.DateThreads.SingleAsync(
            candidate => candidate.Id == seed.ThreadId.Value);
        Assert.Equal(expectedVersion, thread.Version);
        Assert.Equal((short)expectedStatus, thread.Status);
        Assert.Equal(expectedLockedAt, thread.LockedAt);
        Assert.Equal(expectedMessages, await verification.ThreadMessages.CountAsync());
        Assert.Equal(expectedReports, await verification.MessageReports.CountAsync());
        Assert.Equal(expectedModeration, await verification.ThreadModerationEvents.CountAsync());
        Assert.Equal(1, await verification.Notifications.CountAsync());
        Assert.Equal(1, await verification.AuditEvents.CountAsync());
        Assert.Equal(1, await verification.OutboxMessages.CountAsync());
        if (expectedMessageVisibility.HasValue)
        {
            List<ThreadMessageEntity> messages = await verification.ThreadMessages.ToListAsync();
            Assert.All(
                messages,
                message => Assert.Equal((short)expectedMessageVisibility.Value, message.Visibility));
        }
    }

    private static async ValueTask<Result> ExecuteWithinUnitOfWorkAsync(
        PostgresUnitOfWork unitOfWork,
        Func<CancellationToken, ValueTask<Result>> operation)
    {
        Result<bool> result = await unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                Result operationResult = await operation(cancellationToken);
                return operationResult.IsSuccess
                    ? Result.Success(true)
                    : Result.Failure<bool>(operationResult.Error);
            });
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    private static async Task CommitConcurrentPostAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        Membership manager,
        ServiceDate date,
        CancellationToken cancellationToken)
    {
        await using TabrukDbContext writerContext = database.CreateContext();
        PostgresThreadRepository writer = new(writerContext);
        Result<LoadedDateThread> writerLoaded = await writer.GetAsync(
            seed.OrganizationId,
            seed.ThreadId,
            cancellationToken);
        Assert.True(writerLoaded.IsSuccess, writerLoaded.IsFailure ? writerLoaded.Error.Message : null);

        Result<MessagePosted> posted = writerLoaded.Value.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Concurrent post committed after the reader root query.",
            manager,
            date,
            primaryContactSignup: null,
            seed.Now);
        Assert.True(posted.IsSuccess, posted.IsFailure ? posted.Error.Message : null);
        Result saved = await writer.SaveAsync(
            writerLoaded.Value,
            ThreadPersistenceEffects.Empty,
            cancellationToken);
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : null);
    }

    private sealed class ConcurrentPostCommitAfterThreadRootReadInterceptor(
        Func<CancellationToken, Task> commitConcurrentPost) : DbCommandInterceptor
    {
        private int rootReadIntercepted;
        private int concurrentPostCommitted;

        public bool ConcurrentPostCommitted => Volatile.Read(ref concurrentPostCommitted) == 1;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("FROM date_threads", StringComparison.Ordinal)
                || Interlocked.Exchange(ref rootReadIntercepted, 1) != 0)
            {
                return result;
            }

            await commitConcurrentPost(cancellationToken);
            Volatile.Write(ref concurrentPostCommitted, 1);
            return result;
        }
    }

    private sealed class StaleThreadContexts(
        LoadedDateThread first,
        LoadedDateThread second,
        PostgresThreadRepository firstRepository,
        PostgresThreadRepository secondRepository,
        TabrukDbContext firstContext,
        TabrukDbContext secondContext) : IAsyncDisposable
    {
        public LoadedDateThread First { get; } = first;
        public LoadedDateThread Second { get; } = second;
        public PostgresThreadRepository FirstRepository { get; } = firstRepository;
        public PostgresThreadRepository SecondRepository { get; } = secondRepository;
        public PostgresUnitOfWork SecondUnitOfWork { get; } = new(secondContext);

        public async ValueTask DisposeAsync()
        {
            await firstContext.DisposeAsync();
            await secondContext.DisposeAsync();
        }
    }
}
