using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.Core.Batching;

public enum SendScope
{
    Selected,
    AllValid
}

public sealed record RecipientSnapshot(int ImportOrdinal, string Name, string Number);

public sealed record SmsBatchRequest(
    Guid BatchId,
    SendScope Scope,
    IReadOnlyList<RecipientSnapshot> Recipients,
    string Message,
    TwilioCredentials Credentials);

public enum RecipientSendState
{
    Pending,
    InFlight,
    Succeeded,
    Failed,
    CanceledOrNotStarted
}

public sealed record RecipientProgress(
    Guid BatchId,
    int ImportOrdinal,
    RecipientSendState State,
    string? ProviderMessageId,
    string? SafeCode,
    string? SafeMessage);

public enum BatchStartStatus
{
    Completed,
    Canceled,
    RejectedAlreadyActive
}

public sealed record BatchSummary(
    Guid BatchId,
    int Confirmed,
    int Succeeded,
    int Failed,
    int CanceledOrNotStarted);

public sealed record BatchRunResult(
    BatchStartStatus Status,
    BatchSummary? Summary);

public interface IBatchSendCoordinator
{
    Task<BatchRunResult> TryRunAsync(
        SmsBatchRequest request,
        IProgress<RecipientProgress> progress,
        CancellationToken cancellationToken);
}
