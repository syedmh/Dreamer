using Husaynia.Application.Operations.Telemetry;

namespace Husaynia.Application.Operations.Health;

public enum OperationalHealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

public enum HealthComponentKind
{
    Dependency,
    StaleData,
    JobBacklog,
    Internal,
}

public sealed record HealthProbeResult(
    string Name,
    HealthComponentKind Kind,
    OperationalHealthStatus Status,
    string PublicCode,
    TimeSpan? DataAge = null);

public sealed record OperationalHealthReport(
    OperationalHealthStatus Status,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<HealthProbeResult> Components);

public interface IOperationalHealthProbe
{
    Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken);
}

public interface IOperationsDependencyHealthProvider
{
    string Name { get; }

    Task<OperationalHealthStatus> CheckAsync(CancellationToken cancellationToken);
}

public interface IOperationsStalenessProvider
{
    string Name { get; }

    TimeSpan DegradedAfter { get; }

    TimeSpan UnhealthyAfter { get; }

    Task<DateTimeOffset?> GetLastUpdatedAtUtcAsync(CancellationToken cancellationToken);
}

public interface IOperationalHealthService
{
    OperationalHealthReport GetLiveness();

    Task<OperationalHealthReport> GetReadinessAsync(CancellationToken cancellationToken);
}

public sealed class OperationalHealthService(
    IEnumerable<IOperationalHealthProbe> probes,
    IOperationsMetrics metrics,
    TimeProvider timeProvider) : IOperationalHealthService
{
    private readonly IReadOnlyList<IOperationalHealthProbe> probes =
        [.. probes ?? throw new ArgumentNullException(nameof(probes))];
    private readonly IOperationsMetrics metrics =
        metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public OperationalHealthReport GetLiveness() =>
        new(OperationalHealthStatus.Healthy, timeProvider.GetUtcNow(), []);

    public async Task<OperationalHealthReport> GetReadinessAsync(
        CancellationToken cancellationToken)
    {
        if (probes.Count == 0)
        {
            return new OperationalHealthReport(
                OperationalHealthStatus.Unhealthy,
                timeProvider.GetUtcNow(),
                [
                    new HealthProbeResult(
                        "required_probes",
                        HealthComponentKind.Internal,
                        OperationalHealthStatus.Unhealthy,
                        "required_probes_missing"),
                ]);
        }

        var results = new List<HealthProbeResult>(probes.Count);
        foreach (var probe in probes)
        {
            try
            {
                var result = Sanitize(
                    await probe.CheckAsync(cancellationToken).ConfigureAwait(false));
                results.Add(result);
                if (result.Kind == HealthComponentKind.Dependency)
                {
                    metrics.RecordDependency(result.Name, result.Status.ToString());
                }

                if (result.Kind == HealthComponentKind.StaleData && result.DataAge.HasValue)
                {
                    metrics.RecordStaleness(result.Name, result.DataAge.Value);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                results.Add(new HealthProbeResult(
                    probe.GetType().Name,
                    HealthComponentKind.Internal,
                    OperationalHealthStatus.Unhealthy,
                    "probe_failed"));
            }
        }

        var status = results.Any(result => result.Status == OperationalHealthStatus.Unhealthy)
            ? OperationalHealthStatus.Unhealthy
            : results.Any(result => result.Status == OperationalHealthStatus.Degraded)
                ? OperationalHealthStatus.Degraded
                : OperationalHealthStatus.Healthy;
        return new OperationalHealthReport(status, timeProvider.GetUtcNow(), results);
    }

    private static HealthProbeResult Sanitize(HealthProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var validStatus = Enum.IsDefined(result.Status)
            ? result.Status
            : OperationalHealthStatus.Unhealthy;
        var validKind = Enum.IsDefined(result.Kind)
            ? result.Kind
            : HealthComponentKind.Internal;
        return result with
        {
            Name = IsPublicToken(result.Name, 100)
                ? result.Name
                : "operational_component",
            Kind = validKind,
            Status = validStatus,
            PublicCode = IsPublicToken(result.PublicCode, 100)
                ? result.PublicCode
                : "probe_status_unavailable",
            DataAge = result.DataAge is { } dataAge && dataAge < TimeSpan.Zero
                ? TimeSpan.Zero
                : result.DataAge,
        };
    }

    private static bool IsPublicToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '_' or '-' or '.');
}

public sealed class RegisteredDependencyHealthProbe(
    IEnumerable<IOperationsDependencyHealthProvider> providers)
    : IOperationalHealthProbe
{
    private readonly IReadOnlyList<IOperationsDependencyHealthProvider> providers =
        [.. providers ?? throw new ArgumentNullException(nameof(providers))];

    public async Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var status = OperationalHealthStatus.Healthy;
        foreach (var provider in providers)
        {
            var providerStatus = await provider.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (providerStatus > status)
            {
                status = providerStatus;
            }
        }

        return new HealthProbeResult(
            "registered_dependencies",
            HealthComponentKind.Dependency,
            status,
            providers.Count == 0 ? "no_registered_dependencies" : "dependency_status");
    }
}

public sealed class RegisteredStalenessHealthProbe(
    IEnumerable<IOperationsStalenessProvider> providers,
    TimeProvider timeProvider) : IOperationalHealthProbe
{
    private readonly IReadOnlyList<IOperationsStalenessProvider> providers =
        [.. providers ?? throw new ArgumentNullException(nameof(providers))];
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        var status = OperationalHealthStatus.Healthy;
        TimeSpan? oldestAge = null;
        foreach (var provider in providers)
        {
            var updated = await provider.GetLastUpdatedAtUtcAsync(cancellationToken)
                .ConfigureAwait(false);
            var age = updated.HasValue
                ? timeProvider.GetUtcNow() - updated.Value
                : provider.UnhealthyAfter;
            age = age < TimeSpan.Zero ? TimeSpan.Zero : age;
            oldestAge = !oldestAge.HasValue || age > oldestAge ? age : oldestAge;
            var providerStatus = age >= provider.UnhealthyAfter
                ? OperationalHealthStatus.Unhealthy
                : age >= provider.DegradedAfter
                    ? OperationalHealthStatus.Degraded
                    : OperationalHealthStatus.Healthy;
            if (providerStatus > status)
            {
                status = providerStatus;
            }
        }

        return new HealthProbeResult(
            "registered_staleness",
            HealthComponentKind.StaleData,
            status,
            providers.Count == 0 ? "no_registered_staleness" : "staleness_status",
            oldestAge);
    }
}
