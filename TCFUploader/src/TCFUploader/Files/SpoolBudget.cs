namespace TCFUploader.Files;

internal sealed class SpoolBudget
{
    private readonly object gate = new();
    private readonly string spoolDirectory;
    private readonly long aggregateLimitBytes;
    private readonly long minimumFreeSpaceReserveBytes;
    private readonly Func<long> getAvailableFreeSpace;
    private long committedBytes;
    private long reservedBytes;

    internal SpoolBudget(
        string spoolDirectory,
        long aggregateLimitBytes,
        long minimumFreeSpaceReserveBytes,
        Func<long>? getAvailableFreeSpace = null)
    {
        if (aggregateLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(aggregateLimitBytes));
        if (minimumFreeSpaceReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumFreeSpaceReserveBytes));
        this.spoolDirectory = Path.GetFullPath(spoolDirectory);
        this.aggregateLimitBytes = aggregateLimitBytes;
        this.minimumFreeSpaceReserveBytes = minimumFreeSpaceReserveBytes;
        this.getAvailableFreeSpace = getAvailableFreeSpace ?? GetAvailableFreeSpace;
        Directory.CreateDirectory(this.spoolDirectory);
        RefreshCommitted();
    }

    internal long AccountedBytes
    {
        get { lock (gate) return committedBytes + reservedBytes; }
    }

    internal ReservationResult TryReserve(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (gate)
        {
            if (bytes > aggregateLimitBytes - committedBytes - reservedBytes)
                return new ReservationResult.Unavailable("spool_quota_exceeded");
            if (!HasFreeSpace(bytes))
                return new ReservationResult.Unavailable("spool_free_space_reserved");
            reservedBytes += bytes;
            return new ReservationResult.Granted(new Reservation(this, bytes));
        }
    }

    internal bool HasFreeSpaceDuringCopy()
    {
        lock (gate)
            return HasFreeSpace(0);
    }

    internal void ReleaseCommitted(long bytes)
    {
        lock (gate)
            committedBytes = Math.Max(0, committedBytes - bytes);
    }

    internal void RefreshCommitted()
    {
        lock (gate)
        {
            if (reservedBytes != 0)
                throw new InvalidOperationException("Cannot refresh the spool budget while reservations are active.");
            committedBytes = Directory.EnumerateFiles(spoolDirectory, "*.payload")
                .Sum(file => new FileInfo(file).Length);
        }
    }

    private bool HasFreeSpace(long additionalBytes)
    {
        var available = getAvailableFreeSpace();
        return available - additionalBytes >= minimumFreeSpaceReserveBytes;
    }

    private long GetAvailableFreeSpace()
    {
        var root = Path.GetPathRoot(spoolDirectory)
            ?? throw new IOException("The spool directory has no volume root.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private void Commit(long bytes)
    {
        lock (gate)
        {
            reservedBytes -= bytes;
            committedBytes += bytes;
        }
    }

    private void Release(long bytes)
    {
        lock (gate)
            reservedBytes -= bytes;
    }

    internal abstract record ReservationResult
    {
        internal sealed record Granted(Reservation Value) : ReservationResult;
        internal sealed record Unavailable(string OutcomeCode) : ReservationResult;
    }

    internal sealed class Reservation : IDisposable
    {
        private SpoolBudget? owner;
        private readonly long bytes;

        internal Reservation(SpoolBudget owner, long bytes)
        {
            this.owner = owner;
            this.bytes = bytes;
        }

        internal void Commit()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Commit(bytes);
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.Release(bytes);
        }
    }
}
