namespace TCFUploader.Discovery;

using TCFUploader.Files;

internal sealed record ReconcileResult(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> SkippedChildren,
    int SkippedChildCount,
    bool HasMore);

internal sealed class Reconciler(TrustedRoot? trustedRoot = null, int maxTraversalDepth = 1024) : IDisposable
{
    private IEnumerator<ScanEntry>? activeScan;
    private string? activeRoot;
    private string? pendingFile;
    private CancellationToken scanCancellationToken;

    internal Task<ReconcileResult> ScanBatchAsync(
        string watchedRoot,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        var root = Path.GetFullPath(watchedRoot);
        if (trustedRoot is not null &&
            !string.Equals(
                trustedRoot.LexicalPath,
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The supplied root does not match the pinned root.", nameof(watchedRoot));
        if (!Directory.Exists(root) ||
            !(trustedRoot?.VerifyCurrent() ?? PathSecurity.IsSafeWatchRoot(root)))
        {
            ResetScan();
            throw new DirectoryNotFoundException("The watched root is missing or unsafe.");
        }

        if (activeScan is null ||
            !string.Equals(activeRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            ResetScan();
            activeRoot = root;
            activeScan = EnumerateEntries(root).GetEnumerator();
        }

        scanCancellationToken = cancellationToken;
        var files = new List<string>(batchSize);
        var skippedLimit = Math.Min(batchSize, 64);
        var skipped = new List<string>(skippedLimit);
        var skippedCount = 0;

        try
        {
            if (pendingFile is not null)
            {
                files.Add(pendingFile);
                pendingFile = null;
            }

            while (files.Count < batchSize &&
                   MoveNextFile(files, skipped, skippedLimit, ref skippedCount))
            {
            }

            if (files.Count < batchSize)
            {
                ResetScan();
                return Task.FromResult(new ReconcileResult(files, skipped, skippedCount, HasMore: false));
            }

            // One-file lookahead preserves the live enumerator's position without materializing
            // the remaining namespace or restarting from an attacker-influenced lexical cursor.
            var hasMore = MoveNextFile(null, skipped, skippedLimit, ref skippedCount);
            if (!hasMore)
                ResetScan();
            return Task.FromResult(new ReconcileResult(files, skipped, skippedCount, hasMore));
        }
        catch
        {
            ResetScan();
            throw;
        }
    }

    private bool MoveNextFile(
        List<string>? files,
        List<string> skipped,
        int skippedLimit,
        ref int skippedCount)
    {
        while (activeScan!.MoveNext())
        {
            scanCancellationToken.ThrowIfCancellationRequested();
            var entry = activeScan.Current;
            if (entry.IsSkipped)
            {
                skippedCount++;
                if (skipped.Count < skippedLimit)
                    skipped.Add(entry.Path);
                continue;
            }

            if (files is null)
                pendingFile = entry.Path;
            else
                files.Add(entry.Path);
            return true;
        }

        return false;
    }

    private IEnumerable<ScanEntry> EnumerateEntries(string root)
    {
        if (maxTraversalDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTraversalDepth));
        var stack = new Stack<DirectoryFrame>();
        try
        {
            stack.Push(OpenDirectory(root, root, depth: 0));
            while (stack.Count > 0)
            {
                scanCancellationToken.ThrowIfCancellationRequested();
                var frame = stack.Peek();
                string? current = null;
                var enumerationFailed = false;
                try
                {
                    if (!frame.Entries.MoveNext())
                    {
                        stack.Pop().Dispose();
                        continue;
                    }
                    current = frame.Entries.Current;
                }
                catch (Exception ex) when (
                    frame.Depth > 0 &&
                    ex is UnauthorizedAccessException or IOException)
                {
                    enumerationFailed = true;
                }
                if (enumerationFailed)
                {
                    stack.Pop().Dispose();
                    yield return new ScanEntry(frame.Path, IsSkipped: true);
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current!);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        yield return new ScanEntry(current!, IsSkipped: true);
                    continue;
                }
                if (!(trustedRoot?.IsSafeCandidate(current!) ?? PathSecurity.IsSafeCandidate(root, current!)))
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        yield return new ScanEntry(current!, IsSkipped: true);
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (frame.Depth >= maxTraversalDepth)
                    {
                        yield return new ScanEntry(current!, IsSkipped: true);
                        continue;
                    }

                    DirectoryFrame? child = null;
                    try
                    {
                        child = OpenDirectory(root, current!, frame.Depth + 1);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                    }
                    if (child is null)
                        yield return new ScanEntry(current!, IsSkipped: true);
                    else
                        stack.Push(child);
                    continue;
                }

                if (!SupportedMedia.IsSupportedPath(current!))
                    continue;

                yield return new ScanEntry(Path.GetFullPath(current!), IsSkipped: false);
            }
        }
        finally
        {
            while (stack.Count > 0)
                stack.Pop().Dispose();
        }
    }

    private DirectoryFrame OpenDirectory(string root, string directory, int depth)
    {
        scanCancellationToken.ThrowIfCancellationRequested();
        if (!(trustedRoot?.IsSafeCandidate(directory) ?? PathSecurity.IsSafeCandidate(root, directory)))
        {
            if (depth == 0)
                throw new IOException("The watched root became unsafe.");
            throw new UnauthorizedAccessException("A child directory became unsafe.");
        }

        return new DirectoryFrame(
            directory,
            depth,
            Directory.EnumerateFileSystemEntries(directory).GetEnumerator());
    }

    private void ResetScan()
    {
        activeScan?.Dispose();
        activeScan = null;
        activeRoot = null;
        pendingFile = null;
    }

    public void Dispose() => ResetScan();

    private readonly record struct ScanEntry(string Path, bool IsSkipped);

    private sealed record DirectoryFrame(
        string Path,
        int Depth,
        IEnumerator<string> Entries) : IDisposable
    {
        public void Dispose() => Entries.Dispose();
    }
}
