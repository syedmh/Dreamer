using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Prayer;
using Husaynia.Application.Operations.Telemetry;

namespace Husaynia.Application.Tests.Prayer;

public sealed class PrayerRefreshJobTests
{
    [Theory]
    [InlineData("""{"profileHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","year":2026,"month":3,"extra":true}""")]
    [InlineData("""{"ProfileHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","year":2026,"month":3}""")]
    [InlineData("""{"profileHash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","profileHash":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB","year":2026,"month":3}""")]
    public void StrictPayloadRejectsUnknownCaseWrongAndDuplicateFields(string json)
    {
        Assert.Throws<PrayerRefreshJobPayloadException>(
            () => PrayerRefreshJobPayload.Deserialize(json));
    }

    [Fact]
    public void PayloadAndIdempotencyKeyAreCanonical()
    {
        var hash = "A".PadLeft(64, '0');
        var payload = PrayerRefreshJobPayload.Deserialize(
            PrayerRefreshJobPayload.Serialize(hash, new YearMonth(2026, 3)));

        Assert.Equal(hash, payload.ProfileHash);
        Assert.Equal(2026, payload.Year);
        Assert.Equal(3, payload.Month);
        Assert.Equal(
            $"prayer-refresh:{hash}:2026-03",
            PrayerRefreshJobDefinition.IdempotencyKey(hash, new YearMonth(2026, 3)));
    }

    [Fact]
    public async Task HandlerReturnsBoundedFailureAndLetsT19OwnRetry()
    {
        var service = new FailingRefreshService();
        var handler = new PrayerRefreshJobHandler(service);
        var hash = "A".PadLeft(64, '0');

        var result = await handler.ExecuteAsync(
            PrayerRefreshJobPayload.Serialize(hash, new YearMonth(2026, 3)),
            new JobExecutionContext(Guid.NewGuid(), "key", "correlation", 1),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PrayerRefreshTimeout", result.ErrorCode);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task CoordinatorUsesCanonicalMonthlyKeysAndRepairsDeadLetterOnRestart()
    {
        var hash = new string('A', 64);
        var jobs = new RecordingJobStore();
        var correlation = new CorrelationContext();
        using var _ = correlation.Begin("prayer-test", "test");
        var coordinator = new PrayerRefreshJobCoordinator(
            new ActiveProfileStore(hash),
            jobs,
            correlation,
            new FixedTimeProvider(new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero)),
            new PrayerOptions { GenerateMonthsAhead = 2 });

        await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);
        jobs.DeadLettered.Add(jobs.Requests[0].Receipt.JobInstanceId);
        await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);

        Assert.Equal(2, jobs.RegistrationCalls);
        Assert.Equal(4, jobs.Requests.Count);
        Assert.Equal(
            [
                $"prayer-refresh:{hash}:2026-03",
                $"prayer-refresh:{hash}:2026-04",
            ],
            jobs.Requests.Take(2).Select(request => request.Request.IdempotencyKey));
        Assert.Equal(1, jobs.RequeueCalls);
    }

    private sealed class FailingRefreshService : IPrayerRefreshService
    {
        public int Calls { get; private set; }

        public Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RefreshAsync(
            string profileHash,
            YearMonth month,
            IdentityAuditDescriptor? audit,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(
                Result.Fail<PrayerRefreshReceipt, PrayerRefreshError>(
                    new("timeout", "The prayer provider timed out.")));
        }
    }

    private sealed class ActiveProfileStore(string hash) : IPrayerSchedulingStore
        {
            public Task<PrayerActiveProfile?> ReadActiveProfileAsync(
                CancellationToken cancellationToken) =>
                Task.FromResult<PrayerActiveProfile?>(
                    new(hash, new DateOnly(2026, 1, 1)));
        }

    private sealed class RecordingJobStore : IDurableJobStore
        {
            private readonly Dictionary<string, Guid> instances = new(StringComparer.Ordinal);

            public int RegistrationCalls { get; private set; }

            public int RequeueCalls { get; private set; }

            public List<(JobEnqueueRequest Request, JobEnqueueReceipt Receipt)> Requests { get; } = [];

            public HashSet<Guid> DeadLettered { get; } = [];

            public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
                JobDefinitionRegistration registration,
                CancellationToken cancellationToken)
            {
                RegistrationCalls++;
                return Task.FromResult(
                    Result.Succeed<Guid, JobStoreError>(Guid.Parse(
                        "00000000-0000-0000-0000-000000000100")));
            }

            public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
                JobEnqueueRequest request,
                DateTimeOffset now,
                CancellationToken cancellationToken)
            {
                var duplicate = instances.TryGetValue(request.IdempotencyKey, out var id);
                if (!duplicate)
                {
                    id = Guid.NewGuid();
                    instances.Add(request.IdempotencyKey, id);
                }

                var receipt = new JobEnqueueReceipt(id, duplicate);
                Requests.Add((request, receipt));
                return Task.FromResult(
                    Result.Succeed<JobEnqueueReceipt, JobStoreError>(receipt));
            }

            public Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
                Guid jobInstanceId,
                CancellationToken cancellationToken) =>
                Task.FromResult(
                    Result.Succeed<JobStateLookup, JobStoreError>(
                        new(
                            new JobStateSnapshot(
                                jobInstanceId,
                                DeadLettered.Contains(jobInstanceId)
                                    ? "DeadLettered"
                                    : "Completed",
                                1,
                                DateTimeOffset.UtcNow,
                                null,
                                false,
                                null))));

            public Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
                Guid jobInstanceId,
                string actor,
                string reason,
                string correlationId,
                DateTimeOffset now,
                CancellationToken cancellationToken)
            {
                RequeueCalls++;
                DeadLettered.Remove(jobInstanceId);
                return Success(true);
            }

            public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
                WorkerIdentity worker,
                TimeSpan leaseDuration,
                DateTimeOffset now,
                CancellationToken cancellationToken,
                string? definitionKey = null) =>
                throw new NotSupportedException();

            public Task<Result<bool, JobStoreError>> RenewAsync(
                Guid jobInstanceId,
                Guid leaseToken,
                TimeSpan leaseDuration,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<Result<bool, JobStoreError>> CompleteAsync(
                Guid jobInstanceId,
                Guid leaseToken,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<Result<bool, JobStoreError>> FailAsync(
                Guid jobInstanceId,
                Guid leaseToken,
                string errorCode,
                DateTimeOffset? nextRunAtUtc,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
                Guid jobInstanceId,
                Guid leaseToken,
                DateTimeOffset nextRunAtUtc,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
                Guid jobInstanceId,
                DateTimeOffset now,
                CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            private static Task<Result<bool, JobStoreError>> Success(bool value) =>
                Task.FromResult(Result.Succeed<bool, JobStoreError>(value));
        }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
