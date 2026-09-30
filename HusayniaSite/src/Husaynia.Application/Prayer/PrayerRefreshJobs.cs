using System.Text.Json;
using System.Text.Json.Serialization;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Prayer;

namespace Husaynia.Application.Prayer;

public static class PrayerRefreshJobDefinition
{
    public const string Key = "prayer.schedule.refresh.v1";

    public const string Handler = "prayer.schedule.refresh";

    public static JobDefinitionRegistration Registration =>
        new(
            Key,
            Handler,
            MaximumAttempts: 4,
            InitialBackoff: TimeSpan.FromMinutes(1),
            MaximumBackoff: TimeSpan.FromMinutes(30));

    public static string IdempotencyKey(string profileHash, YearMonth month) =>
        $"prayer-refresh:{RequireProfileHash(profileHash)}:{month.Year:D4}-{month.Month:D2}";

    private static string RequireProfileHash(string profileHash)
    {
        if (profileHash is not { Length: PrayerLimits.ProfileHashLength } ||
            profileHash.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterLower(character)))
        {
            throw new ArgumentException("A canonical profile hash is required.", nameof(profileHash));
        }

        return profileHash;
    }
}

public sealed class PrayerRefreshJobPayloadException : ArgumentException
{
    public PrayerRefreshJobPayloadException()
        : base("The prayer refresh job payload is invalid.")
    {
    }
}

public sealed record PrayerRefreshJobPayload(
    [property: JsonPropertyName("profileHash")] string ProfileHash,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month)
{
    private const int MaximumPayloadLength = 512;

    public static string Serialize(string profileHash, YearMonth month)
    {
        _ = PrayerRefreshJobDefinition.IdempotencyKey(profileHash, month);
        return JsonSerializer.Serialize(
            new PrayerRefreshJobPayload(profileHash, month.Year, month.Month));
    }

    public static PrayerRefreshJobPayload Deserialize(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) ||
            payloadJson.Length > MaximumPayloadLength)
        {
            throw new PrayerRefreshJobPayloadException();
        }

        try
        {
            using var document = JsonDocument.Parse(
                payloadJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PrayerRefreshJobPayloadException();
            }

            var properties = document.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 3 ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                !document.RootElement.TryGetProperty("profileHash", out var profileHash) ||
                profileHash.ValueKind != JsonValueKind.String ||
                !document.RootElement.TryGetProperty("year", out var year) ||
                !year.TryGetInt32(out var yearValue) ||
                !document.RootElement.TryGetProperty("month", out var month) ||
                !month.TryGetInt32(out var monthValue))
            {
                throw new PrayerRefreshJobPayloadException();
            }

            var result = new PrayerRefreshJobPayload(
                profileHash.GetString() ?? string.Empty,
                yearValue,
                monthValue);
            _ = PrayerRefreshJobDefinition.IdempotencyKey(
                result.ProfileHash,
                new YearMonth(result.Year, result.Month));
            return result;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException &&
            exception is not PrayerRefreshJobPayloadException)
        {
            throw new PrayerRefreshJobPayloadException();
        }
    }
}

public sealed class PrayerRefreshJobHandler(IPrayerRefreshService refreshService) : IJobHandler
{
    private readonly IPrayerRefreshService refreshService =
        refreshService ?? throw new ArgumentNullException(nameof(refreshService));

    public string HandlerName => PrayerRefreshJobDefinition.Handler;

    public async Task<JobHandlerResult> ExecuteAsync(
        string payloadJson,
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        PrayerRefreshJobPayload payload;
        try
        {
            payload = PrayerRefreshJobPayload.Deserialize(payloadJson);
        }
        catch (PrayerRefreshJobPayloadException)
        {
            return JobHandlerResult.Failed("InvalidPrayerRefreshPayload");
        }

        var result = await refreshService.RefreshAsync(
                payload.ProfileHash,
                new YearMonth(payload.Year, payload.Month),
                audit: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return JobHandlerResult.Succeeded;
        }

        return JobHandlerResult.Failed(result.Error.Code switch
        {
            "timeout" => "PrayerRefreshTimeout",
            "malformed" => "PrayerRefreshMalformed",
            "not_configured" => "PrayerRefreshNotConfigured",
            "profile_not_found" => "PrayerRefreshProfileNotFound",
            _ => "PrayerRefreshUnavailable",
        });
    }
}

public sealed class PrayerRefreshJobCoordinator(
    IPrayerSchedulingStore schedulingStore,
    IDurableJobStore jobStore,
    ICorrelationContext correlation,
    TimeProvider timeProvider,
    PrayerOptions options) : IPrayerRefreshJobCoordinator
{
    private readonly IPrayerSchedulingStore schedulingStore =
        schedulingStore ?? throw new ArgumentNullException(nameof(schedulingStore));
    private readonly IDurableJobStore jobStore =
        jobStore ?? throw new ArgumentNullException(nameof(jobStore));
    private readonly ICorrelationContext correlation =
        correlation ?? throw new ArgumentNullException(nameof(correlation));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly PrayerOptions options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async Task RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken)
    {
        var registration = await jobStore.RegisterDefinitionAsync(
                PrayerRefreshJobDefinition.Registration,
                cancellationToken)
            .ConfigureAwait(false);
        if (registration.IsFailure)
        {
            throw new InvalidOperationException(
                "The prayer refresh job definition could not be registered.");
        }

        var active = await schedulingStore.ReadActiveProfileAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
        {
            await EnsureProfileMonthsAsync(
                    active.ProfileHash,
                    active.EffectiveFrom,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task EnsureProfileMonthsAsync(
        string profileHash,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken)
    {
        var now = TimeZoneInfo.ConvertTime(
            timeProvider.GetUtcNow(),
            PrayerTimeZone.Get());
        var firstMonth = new DateOnly(now.Year, now.Month, 1);
        for (var offset = 0; offset < options.GenerateMonthsAhead; offset++)
        {
            var date = firstMonth.AddMonths(offset);
            if (date.AddMonths(1).AddDays(-1) < effectiveFrom)
            {
                continue;
            }

            var month = new YearMonth(date.Year, date.Month);
            var enqueue = await jobStore.EnqueueAsync(
                    new JobEnqueueRequest(
                        PrayerRefreshJobDefinition.Key,
                        PrayerRefreshJobPayload.Serialize(profileHash, month),
                        PrayerRefreshJobDefinition.IdempotencyKey(profileHash, month),
                        RequiredCorrelationId(correlation.Current.CorrelationId)),
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (enqueue.IsFailure)
            {
                throw new InvalidOperationException(
                    "A prayer refresh job could not be enqueued.");
            }

            if (enqueue.Success.IsDuplicate)
            {
                var state = await jobStore.GetStateAsync(
                        enqueue.Success.JobInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (state.IsFailure)
                {
                    throw new InvalidOperationException(
                        "The prayer refresh job state could not be read.");
                }

                if (string.Equals(
                        state.Success.Snapshot?.State,
                        "DeadLettered",
                        StringComparison.Ordinal))
                {
                    var requeue = await jobStore.RequeueDeadLetterAsync(
                            enqueue.Success.JobInstanceId,
                            "prayer-refresh-repair",
                            "Restore the canonical prayer refresh job.",
                            RequiredCorrelationId(correlation.Current.CorrelationId),
                            timeProvider.GetUtcNow(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (requeue.IsFailure)
                    {
                        throw new InvalidOperationException(
                            "The prayer refresh job could not be repaired.");
                    }
                }
            }
        }
    }

    private static string RequiredCorrelationId(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "prayer-refresh"
            : value.Length <= 128
                ? value
                : value[..128];
}
