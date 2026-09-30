namespace TCFUploader.Files;

internal readonly record struct FileObservation(long Length, DateTime LastWriteUtc);
