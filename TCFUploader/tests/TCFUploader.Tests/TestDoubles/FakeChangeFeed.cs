using TCFUploader.Discovery;

namespace TCFUploader.Tests.TestDoubles;

internal sealed class FakeChangeFeed(
    IEnumerable<FileChange>? changes = null,
    Action? onStart = null,
    Action? onStop = null) : IChangeFeed
{
    private readonly IReadOnlyList<FileChange> startupChanges = changes?.ToArray() ?? [];
    public event Action<FileChange>? Changed;
    public void Start()
    {
        onStart?.Invoke();
        foreach (var change in startupChanges)
            Changed?.Invoke(change);
    }
    public void Stop() => onStop?.Invoke();
    public void Dispose() { }
    internal void Emit(FileChange change) => Changed?.Invoke(change);
}
