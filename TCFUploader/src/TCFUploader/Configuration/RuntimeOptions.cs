namespace TCFUploader.Configuration;

internal sealed record RuntimeOptions(
    TimeSpan ObservationInterval,
    TimeSpan ReconciliationInterval,
    TimeSpan HttpAttemptTimeout,
    TimeSpan ConnectTimeout,
    TimeSpan ShutdownGracePeriod,
    TimeSpan FailedCycleDelay,
    int MaxAttempts,
    int ChangeInboxCapacity,
    int UploadQueueCapacity,
    int MaxJsonResponseBytes,
    long AggregateSpoolLimitBytes,
    long MinimumFreeSpaceReserveBytes,
    int MaxTrackedCandidates,
    int DiscoveryBatchSize,
    long MaxJournalBytes,
    int MaxJournalRecords,
    int MaxJournalRecordBytes)
{
    internal static RuntimeOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        5,
        1024,
        64,
        65_536,
        10L * 1024 * 1024 * 1024,
        2L * 1024 * 1024 * 1024,
        4096,
        4096,
        8L * 1024 * 1024,
        1024,
        256 * 1024);

    internal static RuntimeOptions FromEnvironment(Func<string, string?> readEnvironment)
    {
        var defaults = Default;
        return defaults with
        {
            AggregateSpoolLimitBytes = ReadPositiveInt64(
                readEnvironment, "TCFUPLOADER_MAX_SPOOL_BYTES", defaults.AggregateSpoolLimitBytes),
            MinimumFreeSpaceReserveBytes = ReadNonNegativeInt64(
                readEnvironment, "TCFUPLOADER_MIN_FREE_BYTES", defaults.MinimumFreeSpaceReserveBytes)
        };
    }

    private static long ReadPositiveInt64(
        Func<string, string?> readEnvironment, string name, long defaultValue)
    {
        var value = readEnvironment(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new InvalidDataException($"{name} must be a positive byte count.");
        return parsed;
    }

    private static long ReadNonNegativeInt64(
        Func<string, string?> readEnvironment, string name, long defaultValue)
    {
        var value = readEnvironment(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            throw new InvalidDataException($"{name} must be a non-negative byte count.");
        return parsed;
    }
}
