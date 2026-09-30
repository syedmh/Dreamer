namespace Husaynia.Domain.Operations.Jobs;

public enum JobInstanceState
{
    Pending,
    Running,
    RetryScheduled,
    Completed,
    DeadLettered,
    Cancelled,
}

public enum JobAttemptOutcome
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    LeaseLost,
}

public sealed class JobDefinition
{
    private JobDefinition()
    {
    }

    public JobDefinition(
        string key,
        string handlerName,
        int maximumAttempts,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        {
            throw new ArgumentException("A job definition key within 100 characters is required.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(handlerName) || handlerName.Length > 200)
        {
            throw new ArgumentException("A handler name within 200 characters is required.", nameof(handlerName));
        }

        if (maximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (initialBackoff <= TimeSpan.Zero || maximumBackoff < initialBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff));
        }

        Id = Guid.NewGuid();
        Key = key;
        HandlerName = handlerName;
        MaximumAttempts = maximumAttempts;
        InitialBackoff = initialBackoff;
        MaximumBackoff = maximumBackoff;
        IsEnabled = true;
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string HandlerName { get; private set; } = string.Empty;

    public int MaximumAttempts { get; private set; }

    public TimeSpan InitialBackoff { get; private set; }

    public TimeSpan MaximumBackoff { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Update(
        string handlerName,
        int maximumAttempts,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(handlerName) || handlerName.Length > 200)
        {
            throw new ArgumentException("A handler name within 200 characters is required.", nameof(handlerName));
        }

        if (maximumAttempts is < 1 or > 100 ||
            initialBackoff <= TimeSpan.Zero ||
            maximumBackoff < initialBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        HandlerName = handlerName;
        MaximumAttempts = maximumAttempts;
        InitialBackoff = initialBackoff;
        MaximumBackoff = maximumBackoff;
        IsEnabled = isEnabled;
    }
}

public sealed class JobInstance
{
    private JobInstance()
    {
    }

    public JobInstance(
        Guid definitionId,
        string payloadJson,
        string idempotencyKey,
        string correlationId,
        DateTimeOffset nextRunAtUtc)
    {
        if (definitionId == Guid.Empty)
        {
            throw new ArgumentException("A definition identifier is required.", nameof(definitionId));
        }

        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > 64_000)
        {
            throw new ArgumentException("A bounded payload is required.", nameof(payloadJson));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
        {
            throw new ArgumentException("A bounded idempotency key is required.", nameof(idempotencyKey));
        }

        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new ArgumentException("A bounded correlation identifier is required.", nameof(correlationId));
        }

        Id = Guid.NewGuid();
        DefinitionId = definitionId;
        PayloadJson = payloadJson;
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId;
        State = JobInstanceState.Pending;
        NextRunAtUtc = nextRunAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid DefinitionId { get; private set; }

    public string PayloadJson { get; private set; } = string.Empty;

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public JobInstanceState State { get; private set; }

    public int AttemptsStarted { get; private set; }

    public DateTimeOffset NextRunAtUtc { get; private set; }

    public string? LeaseOwner { get; private set; }

    public Guid? LeaseToken { get; private set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }

    public string? LastErrorCode { get; private set; }

    public Guid Acquire(string worker, DateTimeOffset now, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(worker) || worker.Length > 200)
        {
            throw new ArgumentException("A bounded worker identity is required.", nameof(worker));
        }

        var token = Guid.NewGuid();
        State = JobInstanceState.Running;
        AttemptsStarted++;
        LeaseOwner = worker;
        LeaseToken = token;
        LeaseExpiresAtUtc = now.Add(lease);
        return token;
    }

    public void Renew(DateTimeOffset now, TimeSpan lease) =>
        LeaseExpiresAtUtc = now.Add(lease);

    public void Complete(DateTimeOffset now)
    {
        State = JobInstanceState.Completed;
        CompletedAtUtc = now;
        ClearLease();
    }

    public void ScheduleRetry(DateTimeOffset nextRunAtUtc, string errorCode)
    {
        State = JobInstanceState.RetryScheduled;
        NextRunAtUtc = nextRunAtUtc;
        LastErrorCode = errorCode;
        ClearLease();
    }

    public void DeadLetter(DateTimeOffset now, string errorCode)
    {
        State = JobInstanceState.DeadLettered;
        DeadLetteredAtUtc = now;
        LastErrorCode = errorCode;
        ClearLease();
    }

    public void DeadLetterPreservingError(DateTimeOffset now, string fallbackErrorCode) =>
        DeadLetter(now, LastErrorCode ?? fallbackErrorCode);

    public void ReleaseForRecovery(DateTimeOffset nextRunAtUtc)
    {
        State = JobInstanceState.RetryScheduled;
        NextRunAtUtc = nextRunAtUtc;
        ClearLease();
    }

    public void RequestCancellation(DateTimeOffset now) =>
        CancellationRequestedAtUtc ??= now;

    public void Cancel(DateTimeOffset now)
    {
        State = JobInstanceState.Cancelled;
        CompletedAtUtc = now;
        ClearLease();
    }

    public void Requeue(DateTimeOffset now)
    {
        if (State != JobInstanceState.DeadLettered)
        {
            throw new InvalidOperationException("Only a dead-lettered job can be requeued.");
        }

        State = JobInstanceState.RetryScheduled;
        AttemptsStarted = 0;
        NextRunAtUtc = now;
        DeadLetteredAtUtc = null;
        LastErrorCode = null;
        ClearLease();
    }

    private void ClearLease()
    {
        LeaseOwner = null;
        LeaseToken = null;
        LeaseExpiresAtUtc = null;
    }
}

public sealed class JobAttempt
{
    private JobAttempt()
    {
    }

    public JobAttempt(
        Guid jobInstanceId,
        int number,
        string worker,
        Guid leaseToken,
        DateTimeOffset startedAtUtc)
    {
        Id = Guid.NewGuid();
        JobInstanceId = jobInstanceId;
        Number = number;
        Worker = worker;
        LeaseToken = leaseToken;
        StartedAtUtc = startedAtUtc;
        Outcome = JobAttemptOutcome.Running;
    }

    public Guid Id { get; private set; }

    public Guid JobInstanceId { get; private set; }

    public int Number { get; private set; }

    public string Worker { get; private set; } = string.Empty;

    public Guid LeaseToken { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public JobAttemptOutcome Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public void Finish(JobAttemptOutcome outcome, DateTimeOffset now, string? errorCode = null)
    {
        Outcome = outcome;
        CompletedAtUtc = now;
        ErrorCode = errorCode;
    }
}

public sealed class JobOperationalAudit
{
    private JobOperationalAudit()
    {
    }

    public JobOperationalAudit(
        Guid jobInstanceId,
        string action,
        string actor,
        string reason,
        string correlationId,
        DateTimeOffset occurredAtUtc)
    {
        Id = Guid.NewGuid();
        JobInstanceId = jobInstanceId;
        Action = action;
        Actor = actor;
        Reason = reason;
        CorrelationId = correlationId;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid JobInstanceId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string Actor { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }
}
