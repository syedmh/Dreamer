using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TCFUploader.Configuration;

namespace TCFUploader.Files;

internal sealed class FileSnapshotter : IFileSnapshotter
{
    private readonly string spoolDirectory;
    private readonly string? watchedRoot;
    private readonly TrustedRoot? trustedRoot;
    private readonly TrustedRoot? spoolRootTrust;
    private readonly SpoolBudget spoolBudget;
    private readonly Action<string>? phaseHook;

    internal FileSnapshotter(string spoolDirectory, string? watchedRoot = null)
    {
        this.spoolDirectory = spoolDirectory;
        this.watchedRoot = watchedRoot;
        spoolBudget = new SpoolBudget(
            spoolDirectory,
            RuntimeOptions.Default.AggregateSpoolLimitBytes,
            RuntimeOptions.Default.MinimumFreeSpaceReserveBytes);
    }

    internal FileSnapshotter(
        string spoolDirectory,
        TrustedRoot trustedRoot,
        TrustedRoot spoolRootTrust,
        SpoolBudget spoolBudget,
        Action<string>? phaseHook = null)
    {
        this.spoolDirectory = spoolDirectory;
        this.trustedRoot = trustedRoot;
        this.spoolRootTrust = spoolRootTrust;
        this.spoolBudget = spoolBudget;
        this.phaseHook = phaseHook;
        watchedRoot = trustedRoot.LexicalPath;
    }

    internal FileSnapshotter(string spoolDirectory, TrustedRoot trustedRoot)
    {
        this.spoolDirectory = spoolDirectory;
        this.trustedRoot = trustedRoot;
        spoolBudget = new SpoolBudget(
            spoolDirectory,
            RuntimeOptions.Default.AggregateSpoolLimitBytes,
            RuntimeOptions.Default.MinimumFreeSpaceReserveBytes);
        watchedRoot = trustedRoot.LexicalPath;
    }

    internal FileSnapshotter(string spoolDirectory, SpoolBudget spoolBudget, string? watchedRoot = null)
    {
        this.spoolDirectory = spoolDirectory;
        this.spoolBudget = spoolBudget;
        this.watchedRoot = watchedRoot;
    }

    internal static string NormalizeRelativePath(string relativePath)
    {
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(
            part => string.IsNullOrEmpty(part) || part is "." or ".." || part.Contains(':')))
        {
            throw new ArgumentException("Relative path is invalid.", nameof(relativePath));
        }
        return normalized;
    }

    internal static string CreateRemoteKey(string extension)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return $"tcfuploader-{Convert.ToHexString(bytes).ToLowerInvariant()}{ContentTypeMap.SanitizeExtension(extension)}";
    }

    internal static string ComputeFingerprint(
        string relativePath, long length, DateTime lastWriteUtc, string contentSha256)
    {
        var pathBytes = Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant());
        var hashBytes = Convert.FromHexString(contentSha256);
        var preimage = new byte[4 + pathBytes.Length + 8 + 8 + hashBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(preimage, pathBytes.Length);
        pathBytes.CopyTo(preimage.AsSpan(4));
        BinaryPrimitives.WriteInt64LittleEndian(preimage.AsSpan(4 + pathBytes.Length), length);
        BinaryPrimitives.WriteInt64LittleEndian(preimage.AsSpan(12 + pathBytes.Length), lastWriteUtc.ToUniversalTime().Ticks);
        hashBytes.CopyTo(preimage.AsSpan(20 + pathBytes.Length));
        return Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant();
    }

    public async Task<SnapshotResult> TrySnapshotAsync(
        string canonicalFullPath,
        string normalizedRelativePath,
        FileObservation expected,
        CancellationToken cancellationToken)
    {
        normalizedRelativePath = NormalizeRelativePath(normalizedRelativePath);
        var extension = ContentTypeMap.SanitizeExtension(Path.GetExtension(canonicalFullPath));
        var relativeExtension = ContentTypeMap.SanitizeExtension(Path.GetExtension(normalizedRelativePath));
        if (!SupportedMedia.IsSupportedExtension(extension) ||
            !string.Equals(extension, relativeExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new SnapshotResult.RetryLater(SupportedMedia.UnsupportedOutcomeCode);
        }
        if (spoolRootTrust is null)
            Directory.CreateDirectory(spoolDirectory);
        VerifySpoolStorage();
        var reservationResult = spoolBudget.TryReserve(expected.Length);
        if (reservationResult is SpoolBudget.ReservationResult.Unavailable unavailable)
            return new SnapshotResult.CapacityUnavailable(unavailable.OutcomeCode);
        using var reservation = ((SpoolBudget.ReservationResult.Granted)reservationResult).Value;
        var temporary = Path.Combine(spoolDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            if (watchedRoot is not null && !IsSafeCandidate(canonicalFullPath))
                return new SnapshotResult.RetryLater("source_unsafe");
            await using var source = new FileStream(canonicalFullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (watchedRoot is not null &&
                (!IsSafeCandidate(canonicalFullPath) || !ContainsOpenFile(source)))
                return new SnapshotResult.RetryLater("source_unsafe");
            if (source.Length != expected.Length || File.GetLastWriteTimeUtc(canonicalFullPath) != expected.LastWriteUtc)
            {
                return new SnapshotResult.RetryLater("source_changed");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long count = 0;
            phaseHook?.Invoke("before_create");
            VerifySpoolStorage();
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    phaseHook?.Invoke("before_write");
                    VerifySpoolStorage();
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    count += read;
                    if (!spoolBudget.HasFreeSpaceDuringCopy())
                        return new SnapshotResult.CapacityUnavailable("spool_free_space_reserved");
                }
                await destination.FlushAsync(cancellationToken);
            }

            if (count != expected.Length || File.GetLastWriteTimeUtc(canonicalFullPath) != expected.LastWriteUtc ||
                watchedRoot is not null &&
                (!IsSafeCandidate(canonicalFullPath) || !ContainsOpenFile(source)))
            {
                return new SnapshotResult.RetryLater("source_changed");
            }

            var contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var fingerprint = ComputeFingerprint(normalizedRelativePath, expected.Length, expected.LastWriteUtc, contentHash);
            var final = Path.Combine(spoolDirectory, $"{fingerprint}.payload");
            var payloadCreated = false;
            if (!File.Exists(final))
            {
                phaseHook?.Invoke("before_finalize");
                VerifySpoolStorage();
                File.Move(temporary, final);
                reservation.Commit();
                payloadCreated = true;
                phaseHook?.Invoke("after_finalize");
            }
            return new SnapshotResult.Ready(new SnapshotDescriptor(
                fingerprint, normalizedRelativePath, expected.Length, expected.LastWriteUtc, contentHash,
                count, ContentTypeMap.Get(extension), extension, Path.Combine("spool", $"{fingerprint}.payload")),
                payloadCreated);
        }
        catch (Exception ex) when (
            ex is not SpoolIdentityException &&
            ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return new SnapshotResult.RetryLater("source_unavailable");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                phaseHook?.Invoke("before_cleanup");
                VerifySpoolStorage();
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    private bool IsSafeCandidate(string path) =>
        trustedRoot?.IsSafeCandidate(path) ?? PathSecurity.IsSafeCandidate(watchedRoot!, path);

    private bool ContainsOpenFile(FileStream stream) =>
        trustedRoot?.ContainsOpenFile(stream) ?? PathSecurity.IsOpenFileUnderRoot(watchedRoot!, stream);

    private void VerifySpoolStorage()
    {
        if (spoolRootTrust is not null &&
            (!spoolRootTrust.VerifyCurrent() || !PathSecurity.HasPrivateDirectoryAcl(spoolDirectory)))
        {
            throw new SpoolIdentityException();
        }
    }
}

internal sealed class SpoolIdentityException : IOException
{
    internal SpoolIdentityException()
        : base("The spool directory identity or permissions changed.")
    {
    }
}
