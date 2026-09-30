using System.Text.Json;
using System.Text.Json.Serialization;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Social;

public static class SocialRefreshJobDefinition
{
    public const string Key = "social.feed.refresh.v1";

    public const string Handler = "social.feed.refresh";

    public static JobDefinitionRegistration Registration =>
        new(
            Key,
            Handler,
            MaximumAttempts: 3,
            InitialBackoff: TimeSpan.FromMinutes(1),
            MaximumBackoff: TimeSpan.FromMinutes(15));
}

public sealed class SocialRefreshJobPayloadException
    : ArgumentException
{
    public SocialRefreshJobPayloadException()
        : base("The social refresh job payload is invalid.")
    {
    }
}

public sealed class SocialRefreshJobRegistrationException(
    JobStoreErrorCode errorCode) : InvalidOperationException(
        "The social refresh job definition could not be registered.")
{
    public JobStoreErrorCode ErrorCode { get; } = errorCode;
}

public sealed record SocialRefreshJobPayload(
    [property: JsonPropertyName("provider")] string Provider)
{
    private const int MaximumPayloadLength = 1_024;

    public static string Serialize(string provider) =>
        JsonSerializer.Serialize(
            new SocialRefreshJobPayload(
                SocialFeedValidation.NormalizeProvider(provider)));

    public static SocialRefreshJobPayload Deserialize(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) ||
            payloadJson.Length > MaximumPayloadLength)
        {
            throw new SocialRefreshJobPayloadException();
        }

        try
        {
            using var document = JsonDocument.Parse(
                payloadJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Count() != 1 ||
                !document.RootElement.TryGetProperty("provider", out var provider) ||
                provider.ValueKind != JsonValueKind.String)
            {
                throw new SocialRefreshJobPayloadException();
            }

            return new SocialRefreshJobPayload(
                SocialFeedValidation.NormalizeProvider(provider.GetString() ?? string.Empty));
        }
        catch (JsonException)
        {
            throw new SocialRefreshJobPayloadException();
        }
        catch (ArgumentException exception) when (
            exception is not SocialRefreshJobPayloadException)
        {
            throw new SocialRefreshJobPayloadException();
        }
    }
}

public sealed class SocialRefreshJobHandler(
    ISocialFeedRefreshService refreshService,
    ISocialRefreshJobCoordinator coordinator) : IJobHandler
{
    private readonly ISocialFeedRefreshService refreshService =
        refreshService ?? throw new ArgumentNullException(nameof(refreshService));
    private readonly ISocialRefreshJobCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public SocialRefreshJobHandler(
        ISocialFeedRefreshService refreshService,
        ISocialRefreshJobCoordinator coordinator,
        ISocialSnapshotStore snapshotStore)
        : this(refreshService, coordinator) =>
        ArgumentNullException.ThrowIfNull(snapshotStore);

    public string HandlerName => SocialRefreshJobDefinition.Handler;

    public async Task<JobHandlerResult> ExecuteAsync(
        string payloadJson,
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        SocialRefreshJobPayload payload;
        try
        {
            payload = SocialRefreshJobPayload.Deserialize(payloadJson);
        }
        catch (SocialRefreshJobPayloadException)
        {
            return JobHandlerResult.Failed("InvalidSocialRefreshPayload");
        }

        var execution = await coordinator.PrepareExecutionAsync(
                payload.Provider,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        if (!execution.RefreshRequired)
        {
            return HandlerResult(
                execution.PersistedOutcome ??
                throw new InvalidOperationException(
                    "A persisted social refresh outcome is required."));
        }

        var terminalRecoveryDelay =
            context.AttemptNumber >=
                SocialRefreshJobDefinition.Registration.MaximumAttempts
                ? SocialRefreshJobDefinition.Registration.MaximumBackoff
                : (TimeSpan?)null;
        var receipt = await refreshService.RefreshAsync(
                payload.Provider,
                cancellationToken,
                terminalRecoveryDelay)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        await coordinator.EnsureScheduledAsync(
                payload.Provider,
                cancellationToken)
            .ConfigureAwait(false);
        return HandlerResult(receipt.Outcome);
    }

    private static JobHandlerResult HandlerResult(SocialRefreshOutcome outcome) =>
        outcome is SocialRefreshOutcome.Refreshed or
            SocialRefreshOutcome.RefreshedWithRejectedItems or
            SocialRefreshOutcome.Empty or
            SocialRefreshOutcome.RateLimited
            ? JobHandlerResult.Succeeded
            : JobHandlerResult.Failed(FailureCode(outcome));

    private static string FailureCode(SocialRefreshOutcome outcome) =>
        outcome switch
        {
            SocialRefreshOutcome.TimedOut => "SocialRefreshTimedOut",
            SocialRefreshOutcome.RateLimited => "SocialRefreshRateLimited",
            SocialRefreshOutcome.Unavailable => "SocialRefreshUnavailable",
            SocialRefreshOutcome.Malformed => "SocialRefreshMalformed",
            SocialRefreshOutcome.Cancelled => "SocialRefreshCancelled",
            _ => "SocialRefreshFailed",
        };
}

public sealed record SocialRefreshJobExecutionPlan(
    bool RefreshRequired,
    SocialRefreshOutcome? PersistedOutcome);

public sealed record SocialRefreshJobEnqueueResult(
    string Provider,
    JobEnqueueReceipt Receipt);

public interface ISocialRefreshJobCoordinator
{
    Task<IReadOnlyList<SocialRefreshJobEnqueueResult>> RegisterAndEnqueueCatchUpAsync(
        CancellationToken cancellationToken);

    Task<SocialRefreshJobEnqueueResult> EnsureScheduledAsync(
        string provider,
        CancellationToken cancellationToken);

    Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
        string provider,
        JobExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class SocialRefreshJobCoordinator : ISocialRefreshJobCoordinator
{
    private const string DeadLetteredState = "DeadLettered";
    private const string RepairActor = "social-refresh-repair";
    private const string RepairReason =
        "Restore the canonical recurring social refresh job.";
    private readonly IReadOnlyList<string> providers;
    private readonly ISocialSnapshotStore snapshotStore;
    private readonly IDurableJobStore jobStore;
    private readonly ICorrelationContext correlation;
    private readonly TimeProvider timeProvider;
    private readonly SocialRefreshOptions options;

    public SocialRefreshJobCoordinator(
        IEnumerable<string> providers,
        ISocialSnapshotStore snapshotStore,
        IDurableJobStore jobStore,
        ICorrelationContext correlation,
        TimeProvider timeProvider,
        SocialRefreshOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.providers = providers
            .Select(SocialFeedValidation.NormalizeProvider)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        this.snapshotStore = snapshotStore ??
            throw new ArgumentNullException(nameof(snapshotStore));
        this.jobStore = jobStore ?? throw new ArgumentNullException(nameof(jobStore));
        this.correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.options = options ?? new SocialRefreshOptions();
    }

    public async Task<IReadOnlyList<SocialRefreshJobEnqueueResult>>
        RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken)
    {
        var registration = await jobStore.RegisterDefinitionAsync(
                SocialRefreshJobDefinition.Registration,
                cancellationToken)
            .ConfigureAwait(false);
        if (registration.IsFailure)
        {
            throw new SocialRefreshJobRegistrationException(
                registration.Error.Code);
        }

        var results = new List<SocialRefreshJobEnqueueResult>();
        foreach (var provider in providers)
        {
            results.Add(await EnsureScheduledAsync(provider, cancellationToken)
                .ConfigureAwait(false));
        }

        return results;
    }

    public async Task<SocialRefreshJobEnqueueResult> EnsureScheduledAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var normalizedProvider = ConfiguredProvider(provider);
        var schedulingState = await snapshotStore.ReadSchedulingStateAsync(
                normalizedProvider,
                cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var schedule = BuildSchedule(
            normalizedProvider,
            schedulingState,
            now);
        return await EnqueueAsync(
                normalizedProvider,
                schedule,
                now,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
        string provider,
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.IdempotencyKey) ||
            context.IdempotencyKey.Length > 256)
        {
            throw new InvalidOperationException(
                "The social refresh job idempotency key is invalid.");
        }

        options.Validate();
        var normalizedProvider = ConfiguredProvider(provider);
        var schedulingState = await snapshotStore.ReadSchedulingStateAsync(
                normalizedProvider,
                cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var schedule = BuildSchedule(
            normalizedProvider,
            schedulingState,
            now);
        if (string.Equals(
                context.IdempotencyKey,
                schedule.IdempotencyKey,
                StringComparison.Ordinal))
        {
            return new SocialRefreshJobExecutionPlan(true, null);
        }

        if (IsLegacyGenerationKey(normalizedProvider, context.IdempotencyKey))
        {
            var marker = await ReadLegacyScheduledMarkerAsync(
                    context.JobInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!LegacyGenerationWasProcessed(
                    schedulingState,
                    schedule.RecoveryAnchorUtc,
                    marker))
            {
                return new SocialRefreshJobExecutionPlan(true, null);
            }
        }

        await EnqueueAsync(
                normalizedProvider,
                schedule,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        var persistedOutcome = IsLegacyRecoveryKey(
                normalizedProvider,
                context.IdempotencyKey)
            ? SocialRefreshOutcome.Refreshed
            : PersistedOutcome(schedulingState);
        return new SocialRefreshJobExecutionPlan(false, persistedOutcome);
    }

    private async Task<SocialRefreshJobEnqueueResult> EnqueueAsync(
        string provider,
        SocialRefreshSchedule schedule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var request = new JobEnqueueRequest(
            SocialRefreshJobDefinition.Key,
            SocialRefreshJobPayload.Serialize(provider),
            schedule.IdempotencyKey,
            RequiredCorrelationId(correlation.Current.CorrelationId),
            schedule.NotBeforeUtc);
        var enqueue = await jobStore.EnqueueAsync(request, now, cancellationToken)
            .ConfigureAwait(false);
        if (enqueue.IsFailure)
        {
            throw new InvalidOperationException(
                "The social refresh job could not be enqueued.");
        }

        if (enqueue.Success.IsDuplicate)
        {
            await RepairDeadLetteredCanonicalJobAsync(
                    provider,
                    schedule,
                    enqueue.Success,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new SocialRefreshJobEnqueueResult(
            provider,
            enqueue.Success);
    }

    private async Task RepairDeadLetteredCanonicalJobAsync(
        string provider,
        SocialRefreshSchedule schedule,
        JobEnqueueReceipt receipt,
        CancellationToken cancellationToken)
    {
        var state = await ReadJobStateAsync(
                receipt.JobInstanceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                state?.State,
                DeadLetteredState,
                StringComparison.Ordinal))
        {
            return;
        }

        var currentState = await snapshotStore.ReadSchedulingStateAsync(
                provider,
                cancellationToken)
            .ConfigureAwait(false);
        var currentSchedule = BuildSchedule(
            provider,
            currentState,
            timeProvider.GetUtcNow());
        if (!string.Equals(
                schedule.IdempotencyKey,
                currentSchedule.IdempotencyKey,
                StringComparison.Ordinal))
        {
            return;
        }

        var requeue = await jobStore.RequeueDeadLetterAsync(
                receipt.JobInstanceId,
                RepairActor,
                RepairReason,
                RequiredCorrelationId(correlation.Current.CorrelationId),
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (requeue.IsSuccess && requeue.Success)
        {
            return;
        }

        if (requeue.IsFailure &&
            requeue.Error.Code is JobStoreErrorCode.InvalidState or
                JobStoreErrorCode.LeaseLost &&
            await WasRepairedConcurrentlyAsync(
                    receipt.JobInstanceId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            "The canonical social refresh job could not be requeued.");
    }

    private async Task<bool> WasRepairedConcurrentlyAsync(
        Guid jobInstanceId,
        CancellationToken cancellationToken)
    {
        var state = await ReadJobStateAsync(jobInstanceId, cancellationToken)
            .ConfigureAwait(false);
        return state is not null &&
            !string.Equals(
                state.State,
                DeadLetteredState,
                StringComparison.Ordinal);
    }

    private async Task<JobStateSnapshot?> ReadJobStateAsync(
        Guid jobInstanceId,
        CancellationToken cancellationToken)
    {
        var state = await jobStore.GetStateAsync(jobInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (state.IsFailure)
        {
            throw new InvalidOperationException(
                "The social refresh job state could not be read.");
        }

        return state.Success.Snapshot;
    }

    private async Task<DateTimeOffset> ReadLegacyScheduledMarkerAsync(
        Guid jobInstanceId,
        CancellationToken cancellationToken)
    {
        var state = await ReadJobStateAsync(jobInstanceId, cancellationToken)
            .ConfigureAwait(false);
        return state?.NextRunAtUtc ??
            throw new InvalidOperationException(
                "The legacy social refresh job state was not found.");
    }

    private string ConfiguredProvider(string provider)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        if (!providers.Contains(normalizedProvider, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The social refresh provider is not configured.");
        }

        return normalizedProvider;
    }

    private SocialRefreshSchedule BuildSchedule(
        string provider,
        SocialRefreshSchedulingState schedulingState,
        DateTimeOffset now)
    {
        var effectiveRetryAfter = SocialRateLimitPolicy.BoundStoredRetryAfter(
            schedulingState.RefreshState,
            now,
            options);
        if (effectiveRetryAfter is { } retryAfterUtc)
        {
            return new SocialRefreshSchedule(
                $"social-refresh:{provider}:recovery:v2:" +
                $"not-before:{retryAfterUtc.UtcTicks}",
                retryAfterUtc > now ? retryAfterUtc : now,
                retryAfterUtc);
        }

        if (schedulingState.ActiveSnapshotVersion is not { } version ||
            schedulingState.ActiveSnapshotExpiresAtUtc is not { } expiresAtUtc)
        {
            return new SocialRefreshSchedule(
                $"social-refresh:{provider}:missing",
                now,
                null);
        }

        return new SocialRefreshSchedule(
            $"social-refresh:{provider}:v{version}:e{expiresAtUtc.UtcTicks}",
            expiresAtUtc > now ? expiresAtUtc : now,
            null);
    }

    private static bool LegacyGenerationWasProcessed(
        SocialRefreshSchedulingState schedulingState,
        DateTimeOffset? recoveryAnchorUtc,
        DateTimeOffset scheduledMarkerUtc)
    {
        var refreshState = schedulingState.RefreshState;
        if (refreshState?.LastSuccessAtUtc is { } lastSuccessAtUtc &&
            lastSuccessAtUtc >= scheduledMarkerUtc)
        {
            return true;
        }

        return refreshState?.LastFailureAtUtc is { } lastFailureAtUtc &&
            lastFailureAtUtc >= scheduledMarkerUtc &&
            recoveryAnchorUtc is { } recoveryAnchor &&
            recoveryAnchor > scheduledMarkerUtc;
    }

    private static SocialRefreshOutcome PersistedOutcome(
        SocialRefreshSchedulingState schedulingState) =>
        schedulingState.RefreshState?.LastError switch
        {
            SocialRefreshError.Timeout => SocialRefreshOutcome.TimedOut,
            SocialRefreshError.RateLimited => SocialRefreshOutcome.RateLimited,
            SocialRefreshError.Unavailable => SocialRefreshOutcome.Unavailable,
            SocialRefreshError.Malformed => SocialRefreshOutcome.Malformed,
            SocialRefreshError.Cancelled => SocialRefreshOutcome.Cancelled,
            _ => SocialRefreshOutcome.Refreshed,
        };

    private static bool IsLegacyGenerationKey(
        string provider,
        string idempotencyKey)
    {
        var prefix = $"social-refresh:{provider}:generation:";
        return idempotencyKey.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(idempotencyKey[prefix.Length..], "N", out _);
    }

    private static bool IsLegacyRecoveryKey(
        string provider,
        string idempotencyKey)
    {
        var prefix = $"social-refresh:{provider}:recovery:";
        return idempotencyKey.StartsWith(prefix, StringComparison.Ordinal) &&
            !idempotencyKey.StartsWith(
                $"{prefix}v2:",
                StringComparison.Ordinal);
    }

    private static string RequiredCorrelationId(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new InvalidOperationException(
                "A correlation identifier is required to enqueue a social refresh.");
        }

        var normalized = correlationId.Trim();
        if (normalized.Length > 128 ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException(
                "The correlation identifier is invalid for a social refresh.");
        }

        return normalized;
    }

    private sealed record SocialRefreshSchedule(
        string IdempotencyKey,
        DateTimeOffset NotBeforeUtc,
        DateTimeOffset? RecoveryAnchorUtc);
}
