using Husaynia.Application.Contracts;

namespace Husaynia.Application.Operations.Jobs;

public sealed record JobDefinitionRegistration(
    string Key,
    string HandlerName,
    int MaximumAttempts,
    TimeSpan InitialBackoff,
    TimeSpan MaximumBackoff,
    bool IsEnabled = true);

public sealed record JobEnqueueRequest(
    string DefinitionKey,
    string PayloadJson,
    string IdempotencyKey,
    string CorrelationId,
    DateTimeOffset? NotBeforeUtc = null);

public sealed record JobEnqueueReceipt(Guid JobInstanceId, bool IsDuplicate);

public sealed record AcquiredJob(
    Guid JobInstanceId,
    string DefinitionKey,
    string HandlerName,
    string PayloadJson,
    string IdempotencyKey,
    string CorrelationId,
    int AttemptNumber,
    int MaximumAttempts,
    TimeSpan InitialBackoff,
    TimeSpan MaximumBackoff,
    WorkerIdentity Worker,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc,
    bool CancellationRequested);

public sealed record JobAcquireResult(AcquiredJob? Job);

public sealed record JobStateSnapshot(
    Guid JobInstanceId,
    string State,
    int AttemptsStarted,
    DateTimeOffset NextRunAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    bool CancellationRequested,
    string? LastErrorCode);

public sealed record JobStateLookup(JobStateSnapshot? Snapshot);

public enum JobStoreErrorCode
{
    InvalidInput,
    DefinitionNotFound,
    DuplicateConflict,
    LeaseLost,
    InvalidState,
    PersistenceFailure,
}

public sealed record JobStoreError(JobStoreErrorCode Code, string Message);

public interface IDurableJobStore
{
    Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
        JobDefinitionRegistration registration,
        CancellationToken cancellationToken);

    Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
        JobEnqueueRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
        WorkerIdentity worker,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? definitionKey = null);

    Task<Result<bool, JobStoreError>> RenewAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, JobStoreError>> CompleteAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, JobStoreError>> FailAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        string errorCode,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        DateTimeOffset nextRunAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, JobStoreError>> RequestCancellationAsync(
        Guid jobInstanceId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
        Guid jobInstanceId,
        string actor,
        string reason,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
        Guid jobInstanceId,
        CancellationToken cancellationToken);
}

public interface IJobHandler
{
    string HandlerName { get; }

    Task<JobHandlerResult> ExecuteAsync(
        string payloadJson,
        JobExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record JobHandlerResult
{
    private JobHandlerResult(bool isSuccess, string? errorCode)
    {
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
    }

    public bool IsSuccess { get; }

    public string? ErrorCode { get; }

    public static JobHandlerResult Succeeded { get; } = new(true, null);

    public static JobHandlerResult Failed(string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) ||
            errorCode.Length > 100 ||
            errorCode.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                    character is '_' or '-' or '.')))
        {
            throw new ArgumentException(
                "A bounded machine-readable error code is required.",
                nameof(errorCode));
        }

        return new JobHandlerResult(false, errorCode);
    }
}

public sealed record JobExecutionContext(
    Guid JobInstanceId,
    string IdempotencyKey,
    string CorrelationId,
    int AttemptNumber);

public sealed record JobExecutionOutcome(
    bool Acquired,
    Guid? JobInstanceId,
    string? FinalState,
    string? ErrorCode)
{
    public bool AcquisitionFailed { get; init; }
}

public sealed class DurableJobOptions
{
    public const string SectionName = "Operations:Jobs";

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan LeaseRenewalInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RecoveryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HandlerTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(5);

    public double JitterRatio { get; init; } = 0.2;
}

public interface IJobBackoffPolicy
{
    TimeSpan GetDelay(AcquiredJob job);
}

public sealed class ExponentialJitterBackoffPolicy(
    DurableJobOptions options,
    Random? random = null) : IJobBackoffPolicy
{
    private readonly DurableJobOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly Random random = random ?? Random.Shared;

    public TimeSpan GetDelay(AcquiredJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var exponent = Math.Max(0, job.AttemptNumber - 1);
        var rawTicks = job.InitialBackoff.Ticks * Math.Pow(2, exponent);
        var boundedTicks = Math.Min(rawTicks, job.MaximumBackoff.Ticks);
        var jitter = 1 + ((random.NextDouble() * 2 - 1) * options.JitterRatio);
        return TimeSpan.FromTicks(Math.Max(1, checked((long)(boundedTicks * jitter))));
    }
}
