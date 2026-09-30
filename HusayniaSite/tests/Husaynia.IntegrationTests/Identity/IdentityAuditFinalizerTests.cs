using System.Collections.Concurrent;
using System.Diagnostics;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Web.Areas.Admin.Identity;
using Microsoft.Extensions.Hosting;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityAuditFinalizerTests
{
    private const string SanitizedFailureMessage = "Identity audit finalization failed.";

    [Fact]
    public async Task ClientDisconnectDoesNotCancelAuditFinalization()
    {
        using var requestCancellation = new CancellationTokenSource();
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new RecordingAuditWriter();
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        requestCancellation.Cancel();

        await finalizer.FinalizeOnceAsync(
            CreateDescriptor(),
            PrivilegedAttemptOutcome.Allowed,
            CreateDetails());

        var token = Assert.Single(writer.Tokens);
        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(1, writer.AttemptCount);
    }

    [Fact]
    public async Task ApplicationShutdownCancelsWriterWithoutRetry()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new CancellationObservingAuditWriter();
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);

        var finalization = finalizer.FinalizeOnceAsync(
            CreateDescriptor(),
            PrivilegedAttemptOutcome.Denied,
            CreateDetails());
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.StopApplication();

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(
            () => finalization);

        Assert.Equal(SanitizedFailureMessage, exception.Message);
        Assert.True(writer.Token.IsCancellationRequested);
        Assert.Equal(1, writer.AttemptCount);
    }

    [Fact]
    public async Task NonCompletingWriterTimesOutAfterFiveSecondsWithoutRetry()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new NonCompletingAuditWriter();
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(
            () => finalizer.FinalizeOnceAsync(
                CreateDescriptor(),
                PrivilegedAttemptOutcome.Allowed,
                CreateDetails()));
        stopwatch.Stop();
        writer.Complete();

        Assert.Equal(SanitizedFailureMessage, exception.Message);
        Assert.True(writer.Token.IsCancellationRequested);
        Assert.Equal(1, writer.AttemptCount);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromSeconds(4.5),
            TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task TimeoutWaitsForWriterCancellationPathWithinBoundedGrace()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new DelayedCancellationObservationAuditWriter();
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        var stopwatch = Stopwatch.StartNew();
        var finalization = finalizer.FinalizeOnceAsync(
            CreateDescriptor(),
            PrivilegedAttemptOutcome.Denied,
            CreateDetails());

        await writer.CancellationPathEntered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(finalization.IsCompleted);
        }
        finally
        {
            writer.ReleaseCancellationPath();
        }

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(
            () => finalization.WaitAsync(TimeSpan.FromSeconds(2)));
        stopwatch.Stop();

        Assert.Equal(SanitizedFailureMessage, exception.Message);
        Assert.True(writer.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Equal(1, writer.AttemptCount);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromSeconds(4.5),
            TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task DuplicateClaimPerformsNoSecondWriteOrCallback()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new BlockingAuditWriter();
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        var descriptor = CreateDescriptor();
        var duplicateCallbackCalled = false;

        var firstFinalization = finalizer.FinalizeOnceAsync(
            descriptor,
            PrivilegedAttemptOutcome.Allowed,
            CreateDetails());
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(
            () => finalizer.FinalizeOnceAsync(
                descriptor,
                PrivilegedAttemptOutcome.Denied,
                CreateDetails(),
                _ =>
                {
                    duplicateCallbackCalled = true;
                    return Task.CompletedTask;
                }));
        writer.Release();
        await firstFinalization;

        Assert.Equal(SanitizedFailureMessage, exception.Message);
        Assert.Equal(1, writer.AttemptCount);
        Assert.False(duplicateCallbackCalled);
    }

    [Fact]
    public async Task ClaimKeyUsesBothCorrelationAndActionWithOrdinalComparison()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new RecordingAuditWriter();
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        var descriptor = CreateDescriptor();

        await finalizer.FinalizeOnceAsync(
            descriptor,
            PrivilegedAttemptOutcome.Allowed,
            CreateDetails());
        await finalizer.FinalizeOnceAsync(
            descriptor with { Action = "identity.user.enable" },
            PrivilegedAttemptOutcome.Allowed,
            CreateDetails());
        await finalizer.FinalizeOnceAsync(
            descriptor with { CorrelationId = "CORRELATION-1" },
            PrivilegedAttemptOutcome.Allowed,
            CreateDetails());

        Assert.Equal(3, writer.AttemptCount);
    }

    [Fact]
    public async Task WriterFailureIsSanitizedAndNeverRetried()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var dependencyFailure = new InvalidOperationException(
            "server=private;password=secret");
        var writer = new ThrowingAuditWriter(dependencyFailure);
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        var callbackCalled = false;

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(
            () => finalizer.FinalizeOnceAsync(
                CreateDescriptor(),
                PrivilegedAttemptOutcome.Denied,
                CreateDetails(),
                _ =>
                {
                    callbackCalled = true;
                    return Task.CompletedTask;
                }));

        Assert.Equal(SanitizedFailureMessage, exception.Message);
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
        Assert.Same(dependencyFailure, exception.InnerException);
        Assert.Equal(1, writer.AttemptCount);
        Assert.False(callbackCalled);
    }

    [Fact]
    public async Task CallbackRunsAfterWriterWithTheIdenticalToken()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var order = new ConcurrentQueue<string>();
        var writer = new RecordingAuditWriter(
            (_, _, _, _) =>
            {
                order.Enqueue("writer");
                return Task.CompletedTask;
            });
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);
        CancellationToken callbackToken = default;

        await finalizer.FinalizeOnceAsync(
            CreateDescriptor(),
            PrivilegedAttemptOutcome.Allowed,
            CreateDetails(),
            token =>
            {
                callbackToken = token;
                order.Enqueue("callback");
                return Task.CompletedTask;
            });

        Assert.Equal(["writer", "callback"], order);
        Assert.Equal(Assert.Single(writer.Tokens), callbackToken);
        Assert.False(callbackToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CallbackFailureIsSanitizedAndNeverRetried()
    {
        using var lifetime = new TestHostApplicationLifetime();
        var writer = new RecordingAuditWriter();
        var callbackFailure = new InvalidOperationException("private commit failure");
        var callbackAttempts = 0;
        var finalizer = new IdentityAuditFinalizer(writer, lifetime);

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(
            () => finalizer.FinalizeOnceAsync(
                CreateDescriptor(),
                PrivilegedAttemptOutcome.Allowed,
                CreateDetails(),
                _ =>
                {
                    Interlocked.Increment(ref callbackAttempts);
                    return Task.FromException(callbackFailure);
                }));

        Assert.Equal(SanitizedFailureMessage, exception.Message);
        Assert.Same(callbackFailure, exception.InnerException);
        Assert.Equal(1, writer.AttemptCount);
        Assert.Equal(1, callbackAttempts);
    }

    private static IdentityAuditDescriptor CreateDescriptor() =>
        new(
            ActorId: "actor-1",
            Roles: new HashSet<string>(["SiteAdministrator"], StringComparer.Ordinal),
            Action: "identity.user.disable",
            TargetType: "IdentityUser",
            TargetId: "target-1",
            CorrelationId: "correlation-1");

    private static Dictionary<string, string?> CreateDetails() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["result"] = "disabled",
        };

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => stopped.Token;

        public void StopApplication() => stopping.Cancel();

        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }

    private sealed class RecordingAuditWriter(
        Func<
            IdentityAuditDescriptor,
            PrivilegedAttemptOutcome,
            IReadOnlyDictionary<string, string?>,
            CancellationToken,
            Task>? append = null) : IAuditWriter
    {
        private readonly Func<
            IdentityAuditDescriptor,
            PrivilegedAttemptOutcome,
            IReadOnlyDictionary<string, string?>,
            CancellationToken,
            Task> append = append ?? ((_, _, _, _) => Task.CompletedTask);
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal ConcurrentQueue<CancellationToken> Tokens { get; } = new();

        public Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            Tokens.Enqueue(cancellationToken);
            return append(descriptor, outcome, details, cancellationToken);
        }
    }

    private sealed class CancellationObservingAuditWriter : IAuditWriter
    {
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken Token { get; private set; }

        public async Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            Token = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class NonCompletingAuditWriter : IAuditWriter
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal CancellationToken Token { get; private set; }

        internal void Complete() => completion.TrySetResult();

        public Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            Token = cancellationToken;
            return completion.Task;
        }
    }

    private sealed class DelayedCancellationObservationAuditWriter : IAuditWriter
    {
        private readonly TaskCompletionSource releaseCancellationPath =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal TaskCompletionSource CancellationPathEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseCancellationPath() => releaseCancellationPath.TrySetResult();

        public async Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationPathEntered.TrySetResult();
                await releaseCancellationPath.Task;
                CancellationObserved.TrySetResult();
            }
        }
    }

    private sealed class BlockingAuditWriter : IAuditWriter
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => release.TrySetResult();

        public async Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            Entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ThrowingAuditWriter(Exception failure) : IAuditWriter
    {
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        public Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            return Task.FromException(failure);
        }
    }
}
