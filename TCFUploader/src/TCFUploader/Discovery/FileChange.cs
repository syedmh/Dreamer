namespace TCFUploader.Discovery;

internal enum FileChangeKind { Upsert, Delete, Overflow }
internal readonly record struct FileChange(FileChangeKind Kind, string? FullPath);
