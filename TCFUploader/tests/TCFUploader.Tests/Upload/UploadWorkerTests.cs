using System.Net;
using TCFUploader.Configuration;
using TCFUploader.State;
using TCFUploader.Tests.State;
using TCFUploader.Tests.TestDoubles;
using TCFUploader.Time;
using TCFUploader.Upload;

namespace TCFUploader.Tests.Upload;

[TestClass]
public sealed class UploadWorkerTests
{
    [TestMethod]
    public async Task AC12_PostSuccess_PersistsCompletedAndSkipsRestart()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await using (repository)
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(repository, new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, "token");
            Assert.AreEqual(WorkerOutcome.Completed, await worker.ProcessAsync(snapshot.Fingerprint, default));
            Assert.AreEqual(UploadStatus.Completed, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsFalse(File.Exists(repository.ResolveSpool(repository.GetRequired(snapshot.Fingerprint))));
        }
        var reopened = (StateOpenResult.Success)await StateRepository.OpenAsync(
            paths.Watch, UploaderConstants.EventId, clock, paths.Local, default);
        await using (reopened.Repository)
            Assert.AreEqual(0, reopened.Repository.GetResumableWork(clock.UtcNow).Count);
    }

    [TestMethod]
    public async Task AC13_InvalidResponses_RemainIncompleteAndLogPreciseStage()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [4, 5]);
        await using (repository)
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"missing":"url"}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(repository, new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default), clock, RuntimeOptions.Default, "token");
            using var output = new StringWriter();
            var original = Console.Out;
            Console.SetOut(output);
            WorkerOutcome outcome;
            try
            {
                outcome = await worker.ProcessAsync(snapshot.Fingerprint, default);
            }
            finally
            {
                Console.SetOut(original);
            }
            Assert.AreEqual(WorkerOutcome.Deferred, outcome);
            var item = repository.GetRequired(snapshot.Fingerprint);
            Assert.AreEqual(UploadStatus.PendingPut, item.Status);
            Assert.AreEqual("put_protocol", item.LastCycleOutcome);
            StringAssert.Contains(output.ToString(), "stage=put");
            StringAssert.Contains(output.ToString(), "outcome=put_protocol");
        }
    }

    [TestMethod]
    public async Task AC13_PostFailures_LogExactStageAndOutcomeCodes()
    {
        var cases = new[]
        {
            new PostFailureCase(HttpStatusCode.OK, "{bad", "post_json_invalid"),
            new PostFailureCase(HttpStatusCode.BadRequest, "{}", "post_http_400"),
            new PostFailureCase(HttpStatusCode.OK, "{}", "post_protocol"),
            new PostFailureCase(HttpStatusCode.OK, """{"success":false}""", "post_protocol")
        };

        foreach (var testCase in cases)
        {
            using var paths = new TestPaths();
            var clock = new FakeClock();
            var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(
                paths, clock, [4, 5], $"{testCase.ExpectedOutcome}.bin");
            await using (repository)
            {
                await repository.MarkPutCompleteAsync(
                    snapshot.Fingerprint,
                    new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object"),
                    default);
                var handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(testCase.Status, testCase.Body);
                using var http = new HttpClient(handler);
                var worker = new UploadWorker(
                    repository,
                    new LumaBoothClient(http, RuntimeOptions.Default),
                    new RetryPolicy(clock, RuntimeOptions.Default),
                    clock,
                    RuntimeOptions.Default,
                    "token");
                using var output = new StringWriter();
                var original = Console.Out;
                Console.SetOut(output);
                try
                {
                    Assert.AreEqual(
                        WorkerOutcome.Deferred,
                        await worker.ProcessAsync(snapshot.Fingerprint, default),
                        testCase.ExpectedOutcome);
                }
                finally
                {
                    Console.SetOut(original);
                }

                var item = repository.GetRequired(snapshot.Fingerprint);
                Assert.AreEqual(UploadStatus.PutComplete, item.Status, testCase.ExpectedOutcome);
                Assert.AreEqual(testCase.ExpectedOutcome, item.LastCycleOutcome);
                StringAssert.Contains(output.ToString(), "stage=post");
                StringAssert.Contains(output.ToString(), $"outcome={testCase.ExpectedOutcome}");
            }
        }
    }

    [TestMethod]
    public async Task AC14_BodyDisconnect_RetriesThroughWorkerAndCompletes()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await using (repository)
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DisconnectingStream())
            });
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default),
                clock,
                RuntimeOptions.Default,
                "token");

            Assert.AreEqual(WorkerOutcome.Completed, await worker.ProcessAsync(snapshot.Fingerprint, default));
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual(UploadStatus.Completed, repository.GetRequired(snapshot.Fingerprint).Status);
            CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1) }, clock.Delays);
        }
    }

    [TestMethod]
    public async Task FirstSignal_AfterTransientPut_DoesNotStartSecondAttemptAndRemainsPendingPut()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await using (repository)
        using (var firstSignal = new CancellationTokenSource())
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}", _ => firstSignal.Cancel());
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default),
                clock,
                RuntimeOptions.Default,
                "token");

            Assert.AreEqual(
                WorkerOutcome.Deferred,
                await worker.ProcessAsync(snapshot.Fingerprint, default, firstSignal.Token));
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual(0, clock.Delays.Count);
            Assert.AreEqual(UploadStatus.PendingPut, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsNull(repository.GetRequired(snapshot.Fingerprint).LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task FirstSignal_AfterTransientPost_DoesNotStartSecondAttemptAndRemainsPutComplete()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await repository.MarkPutCompleteAsync(
            snapshot.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object"),
            default);
        await using (repository)
        using (var firstSignal = new CancellationTokenSource())
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}", _ => firstSignal.Cancel());
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default),
                clock,
                RuntimeOptions.Default,
                "token");

            Assert.AreEqual(
                WorkerOutcome.Deferred,
                await worker.ProcessAsync(snapshot.Fingerprint, default, firstSignal.Token));
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual(0, clock.Delays.Count);
            Assert.AreEqual(UploadStatus.PutComplete, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsNull(repository.GetRequired(snapshot.Fingerprint).LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task FirstSignal_DuringPutRetryDelay_StopsPromptlyWithoutRecordingFailure()
    {
        using var paths = new TestPaths();
        var stateClock = new FakeClock();
        var delayClock = new InterruptibleDelayClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, stateClock, [1, 2, 3]);
        await using (repository)
        using (var firstSignal = new CancellationTokenSource())
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}", response =>
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(delayClock, RuntimeOptions.Default),
                stateClock,
                RuntimeOptions.Default,
                "token");

            var processing = worker.ProcessAsync(snapshot.Fingerprint, default, firstSignal.Token);
            await delayClock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstSignal.Cancel();

            Assert.AreEqual(WorkerOutcome.Deferred, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual(UploadStatus.PendingPut, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsNull(repository.GetRequired(snapshot.Fingerprint).LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task FirstSignal_DuringPostRetryDelay_StopsPromptlyWithoutRecordingFailure()
    {
        using var paths = new TestPaths();
        var stateClock = new FakeClock();
        var delayClock = new InterruptibleDelayClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, stateClock, [1, 2, 3]);
        await repository.MarkPutCompleteAsync(
            snapshot.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object"),
            default);
        await using (repository)
        using (var firstSignal = new CancellationTokenSource())
        {
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.ServiceUnavailable, "{}", response =>
                response.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)));
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(delayClock, RuntimeOptions.Default),
                stateClock,
                RuntimeOptions.Default,
                "token");

            var processing = worker.ProcessAsync(snapshot.Fingerprint, default, firstSignal.Token);
            await delayClock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstSignal.Cancel();

            Assert.AreEqual(WorkerOutcome.Deferred, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, handler.Requests.Count);
            Assert.AreEqual(UploadStatus.PutComplete, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsNull(repository.GetRequired(snapshot.Fingerprint).LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task FirstSignal_BeforePutAttemptAdmission_StartsNoRequestAndRemainsPendingPut()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await using (repository)
        using (var firstSignal = new CancellationTokenSource())
        {
            var barrier = new AttemptAdmissionBarrier();
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"url":"/bucket/object"}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default, barrier.WaitAsync),
                clock,
                RuntimeOptions.Default,
                "token");

            var processing = worker.ProcessAsync(snapshot.Fingerprint, default, firstSignal.Token);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstSignal.Cancel();
            barrier.Release.TrySetResult();

            Assert.AreEqual(WorkerOutcome.Deferred, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(0, handler.Requests.Count);
            Assert.AreEqual(UploadStatus.PendingPut, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsNull(repository.GetRequired(snapshot.Fingerprint).LastCycleOutcome);
        }
    }

    [TestMethod]
    public async Task FirstSignal_BeforePostAttemptAdmission_StartsNoRequestAndRemainsPutComplete()
    {
        using var paths = new TestPaths();
        var clock = new FakeClock();
        var (repository, snapshot) = await StateRepositoryTests.CreatePendingAsync(paths, clock, [1, 2, 3]);
        await repository.MarkPutCompleteAsync(
            snapshot.Fingerprint,
            new Uri("https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object"),
            default);
        await using (repository)
        using (var firstSignal = new CancellationTokenSource())
        {
            var barrier = new AttemptAdmissionBarrier();
            var handler = new ScriptedHttpMessageHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"success":true}""");
            using var http = new HttpClient(handler);
            var worker = new UploadWorker(
                repository,
                new LumaBoothClient(http, RuntimeOptions.Default),
                new RetryPolicy(clock, RuntimeOptions.Default, barrier.WaitAsync),
                clock,
                RuntimeOptions.Default,
                "token");

            var processing = worker.ProcessAsync(snapshot.Fingerprint, default, firstSignal.Token);
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstSignal.Cancel();
            barrier.Release.TrySetResult();

            Assert.AreEqual(WorkerOutcome.Deferred, await processing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(0, handler.Requests.Count);
            Assert.AreEqual(UploadStatus.PutComplete, repository.GetRequired(snapshot.Fingerprint).Status);
            Assert.IsNull(repository.GetRequired(snapshot.Fingerprint).LastCycleOutcome);
        }
    }

    private sealed record PostFailureCase(HttpStatusCode Status, string Body, string ExpectedOutcome);

    private sealed class AttemptAdmissionBarrier
    {
        internal TaskCompletionSource Reached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync(int attempt)
        {
            if (attempt != 1)
                return;
            Reached.TrySetResult();
            await Release.Task;
        }
    }

    private sealed class InterruptibleDelayClock : IClock
    {
        internal TaskCompletionSource DelayStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTime UtcNow { get; } =
            new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public int NextJitterMilliseconds(int exclusiveUpperBound) => 0;
    }

    private sealed class DisconnectingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
