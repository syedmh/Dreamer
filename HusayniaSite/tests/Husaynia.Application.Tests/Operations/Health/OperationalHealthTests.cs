using Husaynia.Application.Operations.Health;
using Husaynia.Application.Operations.Telemetry;

namespace Husaynia.Application.Tests.Operations.Health;

public sealed class OperationalHealthTests
{
    [Fact]
    public async Task ReadinessDistinguishesDependencyDegradationAndStaleData()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        var metrics = new RecordingMetrics();
        var service = new OperationalHealthService(
            [
                new FixedProbe(new HealthProbeResult(
                    "email",
                    HealthComponentKind.Dependency,
                    OperationalHealthStatus.Degraded,
                    "dependency_timeout")),
                new FixedProbe(new HealthProbeResult(
                    "prayer",
                    HealthComponentKind.StaleData,
                    OperationalHealthStatus.Degraded,
                    "stale_snapshot",
                    TimeSpan.FromHours(30))),
            ],
            metrics,
            new FixedTimeProvider(now));

        var report = await service.GetReadinessAsync(CancellationToken.None);

        Assert.Equal(OperationalHealthStatus.Degraded, report.Status);
        Assert.Contains(report.Components, component =>
            component.Kind == HealthComponentKind.Dependency &&
            component.PublicCode == "dependency_timeout");
        Assert.Contains(report.Components, component =>
            component.Kind == HealthComponentKind.StaleData &&
            component.PublicCode == "stale_snapshot");
        Assert.Equal(("email", "Degraded"), Assert.Single(metrics.Dependencies));
        Assert.Equal(("prayer", TimeSpan.FromHours(30)), Assert.Single(metrics.Staleness));
    }

    [Fact]
    public async Task ProbeExceptionBecomesNonDiagnosticUnhealthyState()
    {
        var service = new OperationalHealthService(
            [new ThrowingProbe()],
            new RecordingMetrics(),
            TimeProvider.System);

        var report = await service.GetReadinessAsync(CancellationToken.None);

        Assert.Equal(OperationalHealthStatus.Unhealthy, report.Status);
        var component = Assert.Single(report.Components);
        Assert.Equal("probe_failed", component.PublicCode);
        Assert.DoesNotContain("secret", component.PublicCode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyRequiredProbeSetIsUnhealthy()
    {
        var service = new OperationalHealthService(
            [],
            new RecordingMetrics(),
            TimeProvider.System);

        var report = await service.GetReadinessAsync(CancellationToken.None);

        Assert.Equal(OperationalHealthStatus.Unhealthy, report.Status);
        Assert.Equal("required_probes_missing", Assert.Single(report.Components).PublicCode);
    }

    [Fact]
    public async Task HostileProbeFieldsAreReplacedBeforeMetricsOrPublicResponse()
    {
        var metrics = new RecordingMetrics();
        var service = new OperationalHealthService(
            [new FixedProbe(new HealthProbeResult(
                "database payment-secret",
                HealthComponentKind.Dependency,
                OperationalHealthStatus.Degraded,
                "password=payment-secret"))],
            metrics,
            TimeProvider.System);

        var report = await service.GetReadinessAsync(CancellationToken.None);
        var component = Assert.Single(report.Components);

        Assert.Equal("operational_component", component.Name);
        Assert.Equal("probe_status_unavailable", component.PublicCode);
        Assert.DoesNotContain("payment-secret", component.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("payment-secret", component.PublicCode, StringComparison.Ordinal);
        Assert.Equal("operational_component", Assert.Single(metrics.Dependencies).Name);
    }

    private sealed class FixedProbe(HealthProbeResult result) : IOperationalHealthProbe
    {
        public Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingProbe : IOperationalHealthProbe
    {
        public Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret connection string");
    }

    private sealed class RecordingMetrics : IOperationsMetrics
    {
        internal List<(string Name, string Outcome)> Dependencies { get; } = [];

        internal List<(string Name, TimeSpan Age)> Staleness { get; } = [];

        public void RecordDependency(string dependency, string outcome, double durationMilliseconds = 0) =>
            Dependencies.Add((dependency, outcome));

        public void RecordJob(string definition, string outcome, int attempt)
        {
        }

        public void RecordStaleness(string dataSet, TimeSpan age) =>
            Staleness.Add((dataSet, age));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
