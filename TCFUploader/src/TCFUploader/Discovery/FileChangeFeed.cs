namespace TCFUploader.Discovery;

using TCFUploader.Files;

internal sealed class FileChangeFeed : IChangeFeed
{
    private readonly FileSystemWatcher watcher;
    internal FileChangeFeed(string watchedRoot)
    {
        watcher = new FileSystemWatcher(watchedRoot)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        watcher.Created += OnUpsert;
        watcher.Changed += OnUpsert;
        watcher.Renamed += OnRenamed;
        watcher.Deleted += (_, args) => Changed?.Invoke(new FileChange(FileChangeKind.Delete, args.FullPath));
        watcher.Error += (_, _) => Changed?.Invoke(new FileChange(FileChangeKind.Overflow, null));
    }

    public event Action<FileChange>? Changed;
    public void Start() => watcher.EnableRaisingEvents = true;
    public void Stop() => watcher.EnableRaisingEvents = false;
    public void Dispose() => watcher.Dispose();

    internal static FileChange? TranslateUpsert(string fullPath) =>
        SupportedMedia.IsSupportedPath(fullPath)
            ? new FileChange(FileChangeKind.Upsert, fullPath)
            : null;

    internal static IReadOnlyList<FileChange> TranslateRename(string oldFullPath, string newFullPath)
    {
        var changes = new List<FileChange>(2);
        if (SupportedMedia.IsSupportedPath(oldFullPath))
            changes.Add(new FileChange(FileChangeKind.Delete, oldFullPath));
        if (SupportedMedia.IsSupportedPath(newFullPath))
            changes.Add(new FileChange(FileChangeKind.Upsert, newFullPath));
        return changes;
    }

    private void OnUpsert(object sender, FileSystemEventArgs args)
    {
        if (TranslateUpsert(args.FullPath) is { } change)
            Changed?.Invoke(change);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        foreach (var change in TranslateRename(args.OldFullPath, args.FullPath))
            Changed?.Invoke(change);
    }
}
