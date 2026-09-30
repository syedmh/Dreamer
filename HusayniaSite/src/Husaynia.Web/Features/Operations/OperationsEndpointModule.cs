using System.Diagnostics;
using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Health;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Web.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Husaynia.Web.Features.Operations;

public sealed class OperationsEndpointModule : IHusayniaModule, IHusayniaEndpointModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHostedService<DurableJobWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/health/live",
            (HttpContext httpContext,
                IOperationalHealthService health,
                ICorrelationContext correlation) =>
            {
                using var scope = BeginHttpCorrelation(httpContext, correlation, "health.live");
                return Results.Json(ToPublicResponse(health.GetLiveness()));
            });
        endpoints.MapGet(
            "/health/ready",
            async (
                HttpContext httpContext,
                IOperationalHealthService health,
                ICorrelationContext correlation,
                CancellationToken cancellationToken) =>
            {
                using var scope = BeginHttpCorrelation(httpContext, correlation, "health.ready");
                var report = await health.GetReadinessAsync(cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(
                    ToPublicResponse(report),
                    statusCode: report.Status == OperationalHealthStatus.Unhealthy
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status200OK);
            });
    }

    private static IDisposable BeginHttpCorrelation(
        HttpContext httpContext,
        ICorrelationContext correlation,
        string operationName)
    {
        var scope = correlation.Begin(
            httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault(),
            operationName);
        httpContext.Response.Headers["X-Correlation-ID"] = correlation.Current.CorrelationId;
        return scope;
    }

    private static object ToPublicResponse(OperationalHealthReport report) =>
        new
        {
            status = report.Status == OperationalHealthStatus.Unhealthy
                ? "failed"
                : report.Status.ToString().ToLowerInvariant(),
            checkedAtUtc = report.CheckedAtUtc,
            components = report.Components.Select(component => new
            {
                name = component.Name,
                kind = component.Kind.ToString().ToLowerInvariant(),
                status = component.Status.ToString().ToLowerInvariant(),
                code = component.PublicCode,
            }),
        };
}

internal sealed class DurableJobWorker(
        IServiceScopeFactory scopeFactory,
        DurableJobOptions options,
        TimeProvider timeProvider,
        IOperationsMetrics metrics,
        ICorrelationContext correlation,
        ISensitiveDataRedactor redactor,
        ILogger<DurableJobWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, string, Exception?> LogPollFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1901, "DurableJobPollFailed"),
            "Durable job poll failed. CorrelationId={CorrelationId} FailureType={FailureType}");
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly DurableJobOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IOperationsMetrics metrics =
        metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly ICorrelationContext correlation =
        correlation ?? throw new ArgumentNullException(nameof(correlation));
    private readonly ISensitiveDataRedactor redactor =
        redactor ?? throw new ArgumentNullException(nameof(redactor));
    private readonly ILogger<DurableJobWorker> logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly WorkerIdentity worker =
        new($"{Environment.MachineName}:{Environment.ProcessId}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollingInterval, timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            using (correlation.Begin(operationName: "durable_job.poll"))
            {
                try
                {
                    while (await ExecuteOneAsync(stoppingToken).ConfigureAwait(false))
                    {
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    ReportPollFailure(exception.GetType().Name, errorCode: null);
                }
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> ExecuteOneAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<DurableJobProcessor>();
        var outcome = await processor.ExecuteNextAsync(worker, stoppingToken).ConfigureAwait(false);
        if (outcome.AcquisitionFailed)
        {
            ReportPollFailure("JobStoreError", outcome.ErrorCode);
            return false;
        }

        return outcome.Acquired;
    }

    private void ReportPollFailure(string failureType, string? errorCode)
    {
        // The worker owns one poll-boundary metric; processor job metrics begin after acquisition.
        metrics.RecordDependency("durable_job_store", "poll_failed");
        var safeFailureType = redactor.Redact("failure.type", failureType);
        var activity = Activity.Current;
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", safeFailureType);
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            activity?.SetTag("error.code", redactor.Redact("error.code", errorCode));
        }

        LogPollFailure(
            logger,
            correlation.Current.CorrelationId,
            safeFailureType,
            null);
    }
}
