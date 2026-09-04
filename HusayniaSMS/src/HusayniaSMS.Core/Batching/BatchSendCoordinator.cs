using HusayniaSMS.Core.Messaging;

namespace HusayniaSMS.Core.Batching;

public sealed class BatchSendCoordinator(ITwilioTransportFactory transportFactory)
    : IBatchSendCoordinator
{
    private int _isActive;

    public async Task<BatchRunResult> TryRunAsync(
        SmsBatchRequest request,
        IProgress<RecipientProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        if (Interlocked.CompareExchange(ref _isActive, 1, 0) != 0)
        {
            return new(BatchStartStatus.RejectedAlreadyActive, null);
        }

        try
        {
            var recipients = request.Recipients.ToArray();
            foreach (var recipient in recipients)
            {
                progress.Report(new(request.BatchId, recipient.ImportOrdinal,
                    RecipientSendState.Pending, null, null, null));
            }

            var succeeded = 0;
            var failed = 0;
            var canceled = 0;
            var stopRemaining = false;
            var cancellationObserved = false;
            var stopSafeCode = "BatchStopped";
            var stopSafeMessage = "Not sent because Twilio setup must be corrected.";

            await using var transport = transportFactory.Create(request.Credentials);
            for (var index = 0; index < recipients.Length; index++)
            {
                var recipient = recipients[index];
                if (stopRemaining || cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved |= cancellationToken.IsCancellationRequested;
                    for (var remaining = index; remaining < recipients.Length; remaining++)
                    {
                        progress.Report(new(request.BatchId, recipients[remaining].ImportOrdinal,
                            RecipientSendState.CanceledOrNotStarted, null,
                            cancellationObserved ? "Canceled" : stopSafeCode,
                            cancellationObserved
                                ? "Not sent because cancellation was requested."
                                : stopSafeMessage));
                        canceled++;
                    }

                    break;
                }

                progress.Report(new(request.BatchId, recipient.ImportOrdinal,
                    RecipientSendState.InFlight, null, null, null));

                TransportSendResult result;
                var unexpectedTransportFailure = false;
                try
                {
                    result = await transport.SendAsync(
                        new SmsSendRequest(
                            recipient.Number,
                            request.Credentials.SenderMode,
                            request.Credentials.SenderValue,
                            request.Message),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    unexpectedTransportFailure = true;
                    result = new(false, null, TransportFailureKind.NetworkUnknown,
                        TransportFailureKind.NetworkUnknown.ToString(),
                        "The provider outcome is unknown. Verify the provider message log before retrying.");
                }

                if (result.Succeeded)
                {
                    succeeded++;
                    progress.Report(new(request.BatchId, recipient.ImportOrdinal,
                        RecipientSendState.Succeeded, result.ProviderMessageId, null, null));
                }
                else
                {
                    failed++;
                    progress.Report(new(request.BatchId, recipient.ImportOrdinal,
                        RecipientSendState.Failed, null, result.SafeCode, result.SafeMessage));
                    if (result.FailureKind == TransportFailureKind.AuthenticationOrConfiguration)
                    {
                        stopRemaining = true;
                        stopSafeCode = "BatchStopped";
                        stopSafeMessage = "Not sent because Twilio setup must be corrected.";
                    }
                    else if (unexpectedTransportFailure)
                    {
                        stopRemaining = true;
                        stopSafeCode = "DeliveryAmbiguous";
                        stopSafeMessage =
                            "Not sent because an earlier provider outcome is unknown. Verify provider state before starting another batch.";
                    }
                }
            }

            var summary = new BatchSummary(
                request.BatchId,
                recipients.Length,
                succeeded,
                failed,
                canceled);
            if (summary.Succeeded + summary.Failed + summary.CanceledOrNotStarted != summary.Confirmed)
            {
                throw new InvalidOperationException("Batch result totals did not reconcile.");
            }

            cancellationObserved |= cancellationToken.IsCancellationRequested;
            return new(cancellationObserved ? BatchStartStatus.Canceled : BatchStartStatus.Completed,
                summary);
        }
        finally
        {
            Volatile.Write(ref _isActive, 0);
        }
    }
}
