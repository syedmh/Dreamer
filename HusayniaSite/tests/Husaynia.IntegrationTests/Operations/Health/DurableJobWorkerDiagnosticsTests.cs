using System.Diagnostics;
using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Web.Features.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Husaynia.IntegrationTests.Operations.Health;

public sealed class DurableJobWorkerDiagnosticsTests
{
    [Fact]
    public async Task PollFailureEmitsCorrelatedRedactedMetricLogAndActivity()
    {
        const string hostileSecret = "worker-password=hunter2";
        var correlation = new CorrelationContext();
        var metrics = new CapturingMetrics(correlation);
        var logs = new CapturingLoggerProvider();
        var stoppedActivity =
            new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == CorrelationContext.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == "durable_job.poll")
                {
                    stoppedActivity.TrySetResult(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ICorrelationContext>(correlation);
        builder.Services.AddSingleton<IOperationsMetrics>(metrics);
        builder.Services.AddSingleton<ISensitiveDataRedactor, HostileSensitiveDataRedactor>();
        builder.Services.AddSingleton(new DurableJobOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(1),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
            HandlerTimeout = TimeSpan.FromSeconds(1),
            PollingInterval = TimeSpan.FromSeconds(30),
        });
        builder.Services.AddScoped<IDurableJobStore>(
            _ => new AcquireStore(() => throw new InvalidOperationException(hostileSecret)));
        builder.Services.AddScoped<IJobBackoffPolicy, ExponentialJitterBackoffPolicy>();
        builder.Services.AddScoped<DurableJobProcessor>(
            provider => new DurableJobProcessor(
                provider.GetRequiredService<IDurableJobStore>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<DurableJobOptions>(),
                provider.GetRequiredService<IJobBackoffPolicy>(),
                provider.GetRequiredService<IOperationsMetrics>(),
                provider.GetRequiredService<ICorrelationContext>(),
                provider.GetRequiredService<TimeProvider>()));
        new OperationsEndpointModule().AddServices(
            builder.Services,
            new ConfigurationBuilder().Build());
        await using var app = builder.Build();

        await app.StartAsync();
        var log = await logs.PollFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var metricCorrelation =
            await metrics.PollFailureCorrelation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var activity =
            await stoppedActivity.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await app.StopAsync();

        var activityCorrelation = activity.GetTagItem("correlation.id")?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(log.CorrelationId));
        Assert.Equal(log.CorrelationId, metricCorrelation);
        Assert.Equal(log.CorrelationId, activityCorrelation);
        Assert.Equal(nameof(InvalidOperationException), log.FailureType);
        Assert.DoesNotContain(hostileSecret, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedPollFailureEmitsCorrelatedRedactedMetricLogAndActivity()
    {
        const string hostileSecret = "store failure contains password=hunter2";
        var correlation = new CorrelationContext();
        var metrics = new CapturingMetrics(correlation);
        var logs = new CapturingLoggerProvider();
        var stoppedActivity =
            new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == CorrelationContext.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == "durable_job.poll")
                {
                    stoppedActivity.TrySetResult(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ICorrelationContext>(correlation);
        builder.Services.AddSingleton<IOperationsMetrics>(metrics);
        builder.Services.AddSingleton<ISensitiveDataRedactor, HostileSensitiveDataRedactor>();
        builder.Services.AddSingleton(new DurableJobOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(1),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
            HandlerTimeout = TimeSpan.FromSeconds(1),
            PollingInterval = TimeSpan.FromSeconds(30),
        });
        builder.Services.AddScoped<IDurableJobStore>(
            _ => new AcquireStore(() =>
                Result.Fail<JobAcquireResult, JobStoreError>(
                    new JobStoreError(
                        JobStoreErrorCode.PersistenceFailure,
                        hostileSecret))));
        builder.Services.AddScoped<IJobBackoffPolicy, ExponentialJitterBackoffPolicy>();
        builder.Services.AddScoped<DurableJobProcessor>(
            provider => new DurableJobProcessor(
                provider.GetRequiredService<IDurableJobStore>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<DurableJobOptions>(),
                provider.GetRequiredService<IJobBackoffPolicy>(),
                provider.GetRequiredService<IOperationsMetrics>(),
                provider.GetRequiredService<ICorrelationContext>(),
                provider.GetRequiredService<TimeProvider>()));
        new OperationsEndpointModule().AddServices(
            builder.Services,
            new ConfigurationBuilder().Build());
        await using var app = builder.Build();

        await app.StartAsync();
        var log = await logs.PollFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var metricCorrelation =
            await metrics.PollFailureCorrelation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var activity =
            await stoppedActivity.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await app.StopAsync();

        var activityCorrelation = activity.GetTagItem("correlation.id")?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(log.CorrelationId));
        Assert.Equal(log.CorrelationId, metricCorrelation);
        Assert.Equal(log.CorrelationId, activityCorrelation);
        Assert.Equal("JobStoreError", log.FailureType);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            JobStoreErrorCode.PersistenceFailure.ToString(),
            activity.GetTagItem("error.code")?.ToString());
        Assert.Equal(1, metrics.PollFailureCount);
        Assert.DoesNotContain(hostileSecret, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            hostileSecret,
            string.Join(
                "|",
                activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")),
            StringComparison.Ordinal);
    }

    private sealed record CapturedLog(
        string Message,
        string? CorrelationId,
        string? FailureType);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        internal TaskCompletionSource<CapturedLog> PollFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 1901)
                {
                    return;
                }

                var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
                provider.PollFailure.TrySetResult(new CapturedLog(
                    formatter(state, exception),
                    Value(values, "CorrelationId"),
                    Value(values, "FailureType")));
            }

            private static string? Value(
                IReadOnlyList<KeyValuePair<string, object?>>? values,
                string key) =>
                values?.FirstOrDefault(item =>
                    string.Equals(item.Key, key, StringComparison.Ordinal)).Value?.ToString();
        }
    }

    private sealed class CapturingMetrics(
        ICorrelationContext correlation) : IOperationsMetrics
    {
        private int pollFailureCount;

        internal TaskCompletionSource<string> PollFailureCorrelation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int PollFailureCount => Volatile.Read(ref pollFailureCount);

        public void RecordDependency(
            string dependency,
            string outcome,
            double durationMilliseconds = 0)
        {
            if (dependency == "durable_job_store" && outcome == "poll_failed")
            {
                Interlocked.Increment(ref pollFailureCount);
                PollFailureCorrelation.TrySetResult(correlation.Current.CorrelationId);
            }
        }

        public void RecordJob(string definition, string outcome, int attempt)
        {
        }

        public void RecordStaleness(string dataSet, TimeSpan age)
        {
        }
    }

    private sealed class AcquireStore(
        Func<Result<JobAcquireResult, JobStoreError>> acquire) : IDurableJobStore
    {
        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null) =>
            Task.FromResult(acquire());

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

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
            Guid jobInstanceId,
            string actor,
            string reason,
            string correlationId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
            Guid jobInstanceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
