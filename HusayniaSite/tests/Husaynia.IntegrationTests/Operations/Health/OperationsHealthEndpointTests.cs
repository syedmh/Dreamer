using Husaynia.Application.Operations.Health;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Infrastructure.Operations;
using Husaynia.Infrastructure.Operations.Health;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.Web.Features.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

namespace Husaynia.IntegrationTests.Operations.Health;

public sealed class OperationsHealthEndpointTests
{
    [Fact]
    public async Task LivenessReturnsStableHealthyContract()
    {
        await using var app = await CreateHealthApplicationAsync([]);
        var response = await app.GetTestClient().GetAsync("/health/live");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Empty(body.RootElement.GetProperty("components").EnumerateArray());
        Assert.True(body.RootElement.TryGetProperty("checkedAtUtc", out _));
    }

    [Fact]
    public async Task HealthyReadinessReturns200AndHealthyBody()
    {
        await using var app = await CreateHealthApplicationAsync(
            [new FixedProbe(new HealthProbeResult(
                "sql_database",
                HealthComponentKind.Dependency,
                OperationalHealthStatus.Healthy,
                "database_available"))]);

        var response = await app.GetTestClient().GetAsync("/health/ready");
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "database_available",
            body.RootElement.GetProperty("components")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task DegradedReadinessReturns200WithoutDiagnosticLeakage()
    {
        await using var app = await CreateHealthApplicationAsync(
        [
            new FixedProbe(new HealthProbeResult(
                "sql_database",
                HealthComponentKind.Dependency,
                OperationalHealthStatus.Degraded,
                "dependency_slow")),
        ]);

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "endpoint-correlation");
        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "endpoint-correlation",
            response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Contains("dependency_slow", body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"degraded\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret connection string", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedReadinessReturns503AndFailedBody()
    {
        await using var app = await CreateHealthApplicationAsync(
            [new FixedProbe(new HealthProbeResult(
                "sql_database",
                HealthComponentKind.Dependency,
                OperationalHealthStatus.Unhealthy,
                "database_unavailable"))]);

        var response = await app.GetTestClient().GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"status\":\"failed\"", body, StringComparison.Ordinal);
        Assert.Contains("database_unavailable", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostileCorrelationHeaderIsReplacedAndNeverReflected()
    {
        await using var app = await CreateHealthApplicationAsync(
            [new FixedProbe(new HealthProbeResult(
                "sql_database",
                HealthComponentKind.Dependency,
                OperationalHealthStatus.Healthy,
                "database_available"))]);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Correlation-ID",
            "hostile payment-secret");

        var response = await client.GetAsync("/health/ready");
        var correlation = response.Headers.GetValues("X-Correlation-ID").Single();
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("hostile", correlation, StringComparison.Ordinal);
        Assert.DoesNotContain("payment-secret", correlation, StringComparison.Ordinal);
        Assert.DoesNotContain("payment-secret", body, StringComparison.Ordinal);
        Assert.Equal(32, correlation.Length);
    }

    [Fact]
    public void ProductionOperationsGraphContainsRequiredConcreteProbes()
    {
        var services = new ServiceCollection();
        services.AddDbContext<HusayniaDbContext>(options =>
            options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=probe-graph"));
        new OperationsModule().AddServices(
            services,
            new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        var probes = scope.ServiceProvider.GetServices<IOperationalHealthProbe>().ToArray();

        Assert.Contains(probes, probe => probe is SqlDatabaseHealthProbe);
        Assert.Contains(probes, probe => probe is DurableJobHealthProbe);
        Assert.Contains(probes, probe => probe is RegisteredDependencyHealthProbe);
        Assert.Contains(probes, probe => probe is RegisteredStalenessHealthProbe);
    }

    [Fact]
    public void OperationsModulePreservesHostTimeProviderAndDiscoversGenericHandlers()
    {
        var services = new ServiceCollection();
        var timeProvider = new FixedTimeProvider();
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<IJobHandler, TestJobHandler>();
        services.AddDbContext<HusayniaDbContext>(options =>
            options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=probe-graph"));
        new OperationsModule().AddServices(
            services,
            new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.Same(timeProvider, scope.ServiceProvider.GetRequiredService<TimeProvider>());
        Assert.IsType<TestJobHandler>(
            Assert.Single(scope.ServiceProvider.GetServices<IJobHandler>()));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DurableJobProcessor>());
    }

    private static async Task<WebApplication> CreateHealthApplicationAsync(
        IReadOnlyList<IOperationalHealthProbe> probes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
        builder.Services.AddSingleton<IOperationsMetrics, NullMetrics>();
        foreach (var probe in probes)
        {
            builder.Services.AddSingleton(probe);
        }

        builder.Services.AddSingleton<IOperationalHealthService, OperationalHealthService>();
        var app = builder.Build();
        new OperationsEndpointModule().MapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private sealed class FixedProbe(HealthProbeResult result) : IOperationalHealthProbe
    {
        public Task<HealthProbeResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class NullMetrics : IOperationsMetrics
    {
        public void RecordDependency(string dependency, string outcome, double durationMilliseconds = 0)
        {
        }

        public void RecordJob(string definition, string outcome, int attempt)
        {
        }

        public void RecordStaleness(string dataSet, TimeSpan age)
        {
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
    }

    private sealed class TestJobHandler : IJobHandler
    {
        public string HandlerName => "synthetic.operations.health";

        public Task<JobHandlerResult> ExecuteAsync(
            string payloadJson,
            JobExecutionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(JobHandlerResult.Succeeded);
    }
}
