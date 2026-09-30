namespace TCFUploader.Discovery;

internal interface IChangeFeed : IDisposable
{
    event Action<FileChange>? Changed;
    void Start();
    void Stop();
}
