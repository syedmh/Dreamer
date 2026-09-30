using System.Net;
using System.Threading.Channels;
using TCFUploader.Configuration;
using TCFUploader.Discovery;
using TCFUploader.Files;
using TCFUploader.State;
using TCFUploader.Time;
using TCFUploader.Upload;

namespace TCFUploader.Hosting;

internal sealed class WatcherCoordinator(
    RuntimeOptions? runtimeOptions = null,
    IClock? runtimeClock = null,
    Func<HttpMessageHandler>? handlerFactory = null,
    string? localAppData = null,
    Func<string, IChangeFeed>? changeFeedFactory = null,
    CoordinatorMetrics? metrics = null,
    Func<StateRepository, IFileSnapshotter>? snapshotterFactory = null,
    Func<bool>? intakeStopRequested = null)
{
    private readonly RuntimeOptions options = runtimeOptions ?? RuntimeOptions.Default;
    private readonly IClock clock = runtimeClock ?? new SystemClock();

    internal async Task<int> RunAsync(
        string watchedRoot, string token, CancellationToken firstSignal, CancellationToken immediateSignal)
    {
        var open = await OpenStateAsync(watchedRoot, immediateSignal);
        if (open is StateOpenResult.Invalid invalid)
        {
            Log("ERROR", "startup", "-", "state", 0, "-", invalid.Code);
            return ExitCodes.State;
        }
        if (open is StateOpenResult.Locked locked)
        {
            Log("ERROR", "startup", "-", "state", 0, "-", locked.Code);
            return ExitCodes.State;
        }

        return await RunAsync(
            ((StateOpenResult.Success)open).Repository,
            token,
            firstSignal,
            immediateSignal);
    }

    internal Task<StateOpenResult> OpenStateAsync(string watchedRoot, CancellationToken cancellationToken) =>
        StateRepository.OpenAsync(
            watchedRoot,
            UploaderConstants.EventId,
            clock,
            localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            options,
            cancellationToken);

    internal async Task<int> RunAsync(
        StateRepository repository,
        string token,
        CancellationToken firstSignal,
        CancellationToken immediateSignal)
        => await RunAsync(repository, token, null, firstSignal, immediateSignal);

    internal async Task<int> RunAsync(
        StateRepository repository,
        string token,
        string? userId,
        CancellationToken firstSignal,
        CancellationToken immediateSignal)
    {
        await using (repository)
        {
        var watchedRoot = repository.TrustedRoot.LexicalPath;
        using var handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = options.ConnectTimeout
        };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new LumaBoothClient(http, options, userId);
        var retry = new RetryPolicy(clock, options);
        var worker = new UploadWorker(repository, client, retry, clock, options, token);
        var scheduler = new UploadScheduler(
            options.UploadQueueCapacity,
            metrics is null ? null : metrics.ObserveUploadQueue);
        var inbox = Channel.CreateBounded<FileChange>(new BoundedChannelOptions(options.ChangeInboxCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var tracker = new StabilityTracker(options.ObservationInterval, options.MaxTrackedCandidates);
        using var reconciler = new Reconciler(repository.TrustedRoot);
        var snapshotter = snapshotterFactory?.Invoke(repository) ?? new FileSnapshotter(
            repository.SpoolDirectory,
            repository.TrustedRoot,
            repository.SpoolRootTrust,
            repository.SpoolBudget);
        var scheduled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scheduledLock = new object();
        using var feed = changeFeedFactory?.Invoke(watchedRoot) ?? new FileChangeFeed(watchedRoot);
        var reconcileRequested = 1;
        feed.Changed += change =>
        {
            if (change.Kind == FileChangeKind.Overflow || !inbox.Writer.TryWrite(change))
            {
                metrics?.RecordReconciliationRequest();
                Interlocked.Exchange(ref reconcileRequested, 1);
            }
            else
            {
                metrics?.ObserveChangeInbox(inbox.Reader.Count);
            }
        };

        using var intakeCts = CancellationTokenSource.CreateLinkedTokenSource(firstSignal, immediateSignal);
        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(immediateSignal);
        using var attemptAdmission = new AttemptAdmission(firstSignal);
        void StopFatalIntake()
        {
            attemptAdmission.Stop();
            intakeCts.Cancel();
        }
        var authenticationFatal = false;
        var workerFatal = false;
        var workerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var work in scheduler.Reader.ReadAllAsync(activeCts.Token))
                {
                    if (attemptAdmission.IsStopped) break;
                    WorkerOutcome outcome;
                    try
                    {
                        outcome = await worker.ProcessAsync(
                            work.Fingerprint,
                            activeCts.Token,
                            firstSignal,
                            attemptAdmission);
                    }
                    finally { lock (scheduledLock) scheduled.Remove(work.Fingerprint); }
                    if (outcome == WorkerOutcome.AuthenticationFatal)
                    {
                        authenticationFatal = true;
                        StopFatalIntake();
                        break;
                    }
                    if (outcome == WorkerOutcome.StorageFatal)
                    {
                        workerFatal = true;
                        StopFatalIntake();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (activeCts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log("ERROR", "fatal", "-", "worker", 0, "-", ex.GetType().Name);
                workerFatal = true;
                StopFatalIntake();
            }
        }, activeCts.Token);

        var nextReconciliationUtc = clock.UtcNow + options.ReconciliationInterval;
        var watchedRootFatal = false;
        var runtimeFatal = false;
        try
        {
            foreach (var work in repository.GetResumableWork(clock.UtcNow))
            {
                lock (scheduledLock) scheduled.Add(work.Fingerprint);
                await scheduler.ScheduleAsync(work, intakeCts.Token);
            }

            feed.Start();
            Log("INFO", "monitoring", "-", "startup", 0, "-", "active");
            while (!intakeCts.IsCancellationRequested && !(intakeStopRequested?.Invoke() ?? false))
            {
                if (clock.UtcNow >= nextReconciliationUtc)
                {
                    Interlocked.Exchange(ref reconcileRequested, 1);
                    nextReconciliationUtc = clock.UtcNow + options.ReconciliationInterval;
                }
                if (Interlocked.Exchange(ref reconcileRequested, 0) == 1)
                {
                    metrics?.RecordReconciliation();
                    var scan = await reconciler.ScanBatchAsync(
                        watchedRoot, options.DiscoveryBatchSize, intakeCts.Token);
                    foreach (var child in scan.SkippedChildren)
                    {
                        Log("WARN", "discovery", SafeRelative(watchedRoot, child),
                            "reconcile", 0, "-", "child_skipped");
                    }
                    if (scan.SkippedChildCount > scan.SkippedChildren.Count)
                    {
                        Log("WARN", "discovery", "-", "reconcile", 0, "-",
                            $"children_skipped_{scan.SkippedChildCount}");
                    }
                    var admissionFull = false;
                    foreach (var file in scan.Files)
                    {
                        if (!tracker.Register(file, clock.UtcNow))
                        {
                            admissionFull = true;
                            break;
                        }
                    }
                    metrics?.ObserveCandidates(tracker.Count);
                    if (scan.HasMore || admissionFull)
                    {
                        metrics?.RecordReconciliationRequest();
                        Interlocked.Exchange(ref reconcileRequested, 1);
                    }
                    foreach (var work in repository.GetResumableWork(clock.UtcNow))
                    {
                        var admit = false;
                        lock (scheduledLock) admit = scheduled.Add(work.Fingerprint);
                        if (admit) await scheduler.ScheduleAsync(work, intakeCts.Token);
                    }
                }

                while (inbox.Reader.TryRead(out var change))
                {
                    if (change.FullPath is not null &&
                        (change.Kind == FileChangeKind.Delete ||
                         change.Kind == FileChangeKind.Upsert &&
                         !SupportedMedia.IsSupportedPath(change.FullPath)))
                    {
                        tracker.Remove(change.FullPath);
                    }
                    else if (change.Kind == FileChangeKind.Upsert && change.FullPath is not null &&
                             !tracker.Register(change.FullPath, clock.UtcNow))
                    {
                        metrics?.RecordReconciliationRequest();
                        Interlocked.Exchange(ref reconcileRequested, 1);
                    }
                }

                foreach (var stable in tracker.Observe(clock.UtcNow, path => TryObserve(repository.TrustedRoot, path)))
                {
                    var relative = Path.GetRelativePath(watchedRoot, stable.CanonicalFullPath);
                    SnapshotResult result;
                    try
                    {
                        result = await snapshotter.TrySnapshotAsync(
                            stable.CanonicalFullPath,
                            relative,
                            stable.Observation,
                            intakeCts.Token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log("ERROR", "snapshot", relative, "snapshot", 0, "-",
                            "snapshot_storage_failed");
                        workerFatal = true;
                        StopFatalIntake();
                        break;
                    }
                    if (result is SnapshotResult.RetryLater retryLater)
                    {
                        Log("WARN", "snapshot", relative, "snapshot", 0, "-", retryLater.OutcomeCode);
                        if (retryLater.OutcomeCode != SupportedMedia.UnsupportedOutcomeCode)
                            tracker.Register(stable.CanonicalFullPath, clock.UtcNow);
                        continue;
                    }
                    if (result is SnapshotResult.CapacityUnavailable capacity)
                    {
                        Log("WARN", "snapshot", relative, "snapshot", 0, "-", capacity.OutcomeCode);
                        metrics?.RecordReconciliationRequest();
                        Interlocked.Exchange(ref reconcileRequested, 1);
                        continue;
                    }
                    var ready = (SnapshotResult.Ready)result;
                    var knownStatus = repository.GetKnownStatus(ready.Snapshot.Fingerprint);
                    if (knownStatus is not null)
                    {
                        if (ready.PayloadCreated && knownStatus == UploadStatus.Completed)
                        {
                            try
                            {
                                repository.DeleteNewCompletedDuplicateSpool(ready.Snapshot);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                Log("ERROR", "snapshot", relative, "cleanup", 0,
                                    ready.Snapshot.Fingerprint[..12], "duplicate_spool_cleanup_failed");
                                workerFatal = true;
                                StopFatalIntake();
                                break;
                            }
                        }
                        Log("INFO", "snapshot", relative, "snapshot", 0,
                            ready.Snapshot.Fingerprint[..12], "unchanged_skipped");
                        continue;
                    }
                    var remoteKey = FileSnapshotter.CreateRemoteKey(ready.Snapshot.Extension);
                    try
                    {
                        await repository.AddPendingAsync(ready.Snapshot, remoteKey, intakeCts.Token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log("ERROR", "snapshot", relative, "state", 0,
                            ready.Snapshot.Fingerprint[..12], "pending_state_persist_failed");
                        workerFatal = true;
                        StopFatalIntake();
                        break;
                    }
                    lock (scheduledLock) scheduled.Add(ready.Snapshot.Fingerprint);
                    await scheduler.ScheduleAsync(new UploadWork(ready.Snapshot.Fingerprint), intakeCts.Token);
                }

                metrics?.ObserveCandidates(tracker.Count);
                tracker.Prune(clock.UtcNow - TimeSpan.FromTicks(
                    Math.Max(options.ReconciliationInterval.Ticks * 2, options.ObservationInterval.Ticks * 4)));
                await clock.DelayAsync(options.ObservationInterval, intakeCts.Token);
            }
        }
        catch (OperationCanceledException) when (intakeCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            StopFatalIntake();
            Log("ERROR", "fatal", "-", "discovery", 0, "-", "watched_root_unavailable");
            watchedRootFatal = true;
        }
        catch (Exception ex)
        {
            StopFatalIntake();
            Log("ERROR", "fatal", "-", "coordinator", 0, "-", ex.GetType().Name);
            runtimeFatal = true;
        }
        finally
        {
            try { feed.Stop(); }
            catch (Exception ex)
            {
                StopFatalIntake();
                Log("ERROR", "fatal", "-", "feed_stop", 0, "-", ex.GetType().Name);
                runtimeFatal = true;
            }
            scheduler.Complete();
        }

        if (immediateSignal.IsCancellationRequested)
        {
            activeCts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
            return ExitCodes.ForcedShutdown;
        }

        var graceful = await Task.WhenAny(workerTask, Task.Delay(options.ShutdownGracePeriod, CancellationToken.None));
        if (graceful != workerTask)
        {
            activeCts.Cancel();
            try { await workerTask; } catch (OperationCanceledException) { }
            return watchedRootFatal || runtimeFatal || workerFatal
                ? ExitCodes.Fatal
                : ExitCodes.ForcedShutdown;
        }
        await workerTask;
        if (authenticationFatal) return ExitCodes.Authentication;
        if (watchedRootFatal || runtimeFatal || workerFatal) return ExitCodes.Fatal;
        Log("INFO", "shutdown", "-", "coordinator", 0, "-", "clean");
        return ExitCodes.Success;
        }
    }

    private static FileObservation? TryObserve(TrustedRoot trustedRoot, string path)
    {
        try
        {
            if (!trustedRoot.IsSafeCandidate(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!trustedRoot.IsSafeCandidate(path) || !trustedRoot.ContainsOpenFile(stream)) return null;
            return new FileObservation(info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static void Log(string level, string operation, string path, string stage, int attempt, string fingerprint, string outcome) =>
        Console.WriteLine($"{DateTime.UtcNow:O} {level} operation={operation} path=\"{Escape(path)}\" stage={stage} attempt={attempt} fingerprint={fingerprint} outcome={outcome}");
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string SafeRelative(string watchedRoot, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(watchedRoot, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? "-" : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "-";
        }
    }
}

internal sealed class CoordinatorMetrics
{
    private int changeInboxHighWater;
    private int uploadQueueHighWater;
    private int reconciliationRequests;
    private int reconciliations;
    private int candidateHighWater;

    internal int ChangeInboxHighWater => Volatile.Read(ref changeInboxHighWater);
    internal int UploadQueueHighWater => Volatile.Read(ref uploadQueueHighWater);
    internal int ReconciliationRequests => Volatile.Read(ref reconciliationRequests);
    internal int Reconciliations => Volatile.Read(ref reconciliations);
    internal int CandidateHighWater => Volatile.Read(ref candidateHighWater);

    internal void ObserveChangeInbox(int count) => SetMaximum(ref changeInboxHighWater, count);
    internal void ObserveUploadQueue(int count) => SetMaximum(ref uploadQueueHighWater, count);
    internal void RecordReconciliationRequest() => Interlocked.Increment(ref reconciliationRequests);
    internal void RecordReconciliation() => Interlocked.Increment(ref reconciliations);
    internal void ObserveCandidates(int count) => SetMaximum(ref candidateHighWater, count);

    private static void SetMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }
}
