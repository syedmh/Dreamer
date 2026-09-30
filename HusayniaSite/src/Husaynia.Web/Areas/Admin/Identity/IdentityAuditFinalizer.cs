using System.Collections.Concurrent;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;

namespace Husaynia.Web.Areas.Admin.Identity;

public sealed class IdentityAuditFinalizer : IIdentityAuditFinalizer
{
    private const string FailureMessage = "Identity audit finalization failed.";
    private static readonly TimeSpan FinalizationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancellationObservationGrace = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<(string CorrelationId, string Action), byte> claims = new();
    private readonly IAuditWriter auditWriter;
    private readonly IHostApplicationLifetime applicationLifetime;

    public IdentityAuditFinalizer(
        IAuditWriter auditWriter,
        IHostApplicationLifetime applicationLifetime)
    {
        this.auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        this.applicationLifetime =
            applicationLifetime ?? throw new ArgumentNullException(nameof(applicationLifetime));
    }

    public async Task FinalizeOnceAsync(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details,
        Func<CancellationToken, Task>? completePersistenceAsync = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(details);

        if (!claims.TryAdd((descriptor.CorrelationId, descriptor.Action), 0))
        {
            throw new IdentityAuditFinalizationException(FailureMessage);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            applicationLifetime.ApplicationStopping);
        cancellation.CancelAfter(FinalizationTimeout);
        var cancellationToken = cancellation.Token;

        try
        {
            var appendTask = auditWriter.AppendAsync(
                descriptor,
                outcome,
                details,
                cancellationToken);
            try
            {
                await appendTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ObserveCancellationCompletionAsync(appendTask).ConfigureAwait(false);
                throw;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (completePersistenceAsync is not null)
            {
                await completePersistenceAsync(cancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            throw new IdentityAuditFinalizationException(FailureMessage, exception);
        }
    }

    private static async Task ObserveCancellationCompletionAsync(Task appendTask)
    {
        var completedTask = await Task.WhenAny(
                appendTask,
                Task.Delay(CancellationObservationGrace))
            .ConfigureAwait(false);
        if (ReferenceEquals(completedTask, appendTask) && !appendTask.IsCanceled)
        {
            await appendTask.ConfigureAwait(false);
        }
    }
}
