using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeContactCsvStore : IContactCsvStore
{
    private readonly Queue<ContactCsvLoadResult> _loadResults = [];
    private readonly Queue<ContactCsvSaveResult> _saveResults = [];
    private readonly TaskCompletionSource _loadRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _saveRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool DelayLoad { get; set; }
    public bool DelaySave { get; set; }
    public List<string> LoadPaths { get; } = [];
    public List<ContactCsvSaveRequest> SaveRequests { get; } = [];
    public TaskCompletionSource LoadStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SaveStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void QueueLoad(params ContactCsvLoadResult[] results)
    {
        foreach (var result in results)
        {
            _loadResults.Enqueue(result);
        }
    }

    public void QueueSave(params ContactCsvSaveResult[] results)
    {
        foreach (var result in results)
        {
            _saveResults.Enqueue(result);
        }
    }

    public async Task<ContactCsvLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        LoadPaths.Add(path);
        LoadStarted.TrySetResult();
        if (DelayLoad)
        {
            await _loadRelease.Task.WaitAsync(cancellationToken);
        }

        return _loadResults.Count == 0
            ? FailureLoad("No fake load result was queued.")
            : _loadResults.Dequeue();
    }

    public async Task<ContactCsvSaveResult> SaveAsync(
        ContactCsvSaveRequest request,
        CancellationToken cancellationToken)
    {
        SaveRequests.Add(request);
        SaveStarted.TrySetResult();
        if (DelaySave)
        {
            await _saveRelease.Task.WaitAsync(cancellationToken);
        }

        return _saveResults.Count == 0
            ? new(
                ContactCsvSaveStatus.IoFailure,
                null,
                null,
                null,
                "No fake save result was queued.")
            : _saveResults.Dequeue();
    }

    public void ReleaseLoad() => _loadRelease.TrySetResult();
    public void ReleaseSave() => _saveRelease.TrySetResult();

    public static ContactCsvLoadResult SuccessfulLoad(
        string path,
        IReadOnlyList<ContactRow> rows,
        ContactCsvVersion? version = null) =>
        new(
            CsvImportStatus.Success,
            path,
            rows,
            version ?? Version(rows.Count, 'A'),
            null);

    public static ContactCsvSaveResult SuccessfulSave(
        string path,
        ContactCsvVersion? version = null) =>
        new(
            ContactCsvSaveStatus.Saved,
            path,
            version ?? Version(1, 'B'),
            null,
            null);

    public static ContactCsvVersion Version(long length, char hashCharacter) =>
        new(
            length,
            new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero),
            new string(hashCharacter, 64));

    private static ContactCsvLoadResult FailureLoad(string diagnostic) =>
        new(
            CsvImportStatus.IoFailure,
            null,
            Array.Empty<ContactRow>(),
            null,
            diagnostic);
}
