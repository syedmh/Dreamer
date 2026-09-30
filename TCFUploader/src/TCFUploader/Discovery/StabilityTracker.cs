using TCFUploader.Files;
using TCFUploader.Configuration;

namespace TCFUploader.Discovery;

internal readonly record struct StableCandidate(string CanonicalFullPath, FileObservation Observation);

internal sealed class StabilityTracker
{
    private const int MaxQualifyingObservationsPerAdmission = 6;
    private readonly TimeSpan interval;
    private readonly int capacity;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    internal StabilityTracker(TimeSpan interval, int capacity)
    {
        this.interval = interval;
        this.capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }
    internal StabilityTracker(TimeSpan interval) : this(interval, RuntimeOptions.Default.MaxTrackedCandidates) { }

    internal bool Register(string canonicalFullPath, DateTime observedUtc)
    {
        var path = Path.GetFullPath(canonicalFullPath);
        if (entries.TryGetValue(path, out var existing))
        {
            existing.LastSeenUtc = observedUtc;
            return true;
        }
        if (entries.Count >= capacity)
            return false;
        entries.Add(path, new Entry { LastSeenUtc = observedUtc });
        return true;
    }
    internal bool Register(string canonicalFullPath) => Register(canonicalFullPath, DateTime.UtcNow);

    internal void Remove(string canonicalFullPath) => entries.Remove(Path.GetFullPath(canonicalFullPath));
    internal int Count => entries.Count;

    internal IReadOnlyList<StableCandidate> Observe(
        DateTime observedUtc,
        Func<string, FileObservation?> tryObserveAndOpen)
    {
        var stable = new List<StableCandidate>();
        foreach (var pair in entries.ToArray())
        {
            var observation = tryObserveAndOpen(pair.Key);
            var entry = pair.Value;
            if (observation is null)
            {
                entry.UnavailableObservations++;
                entry.ResetObservation();
                if (entry.UnavailableObservations >= 3)
                    entries.Remove(pair.Key);
                continue;
            }
            entry.UnavailableObservations = 0;
            entry.LastSeenUtc = observedUtc;

            if (entry.Observation != observation)
            {
                entry.Observation = observation;
                entry.Count = 1;
                entry.LastQualifyingUtc = observedUtc;
                entry.AdmissionObservations++;
                if (entry.AdmissionObservations >= MaxQualifyingObservationsPerAdmission)
                    entries.Remove(pair.Key);
                continue;
            }

            if (observedUtc - entry.LastQualifyingUtc < interval) { continue; }
            entry.Count++;
            entry.AdmissionObservations++;
            entry.LastQualifyingUtc = observedUtc;
            if (entry.Count >= 3)
            {
                stable.Add(new StableCandidate(pair.Key, observation.Value));
                entries.Remove(pair.Key);
            }
            else if (entry.AdmissionObservations >= MaxQualifyingObservationsPerAdmission)
            {
                entries.Remove(pair.Key);
            }
        }
        return stable;
    }

    internal int Prune(DateTime olderThanUtc)
    {
        var stale = entries.Where(pair => pair.Value.LastSeenUtc < olderThanUtc)
            .Select(pair => pair.Key).ToArray();
        foreach (var path in stale)
            entries.Remove(path);
        return stale.Length;
    }

    private sealed class Entry
    {
        internal FileObservation? Observation { get; set; }
        internal int Count { get; set; }
        internal DateTime LastQualifyingUtc { get; set; }
        internal DateTime LastSeenUtc { get; set; }
        internal int UnavailableObservations { get; set; }
        internal int AdmissionObservations { get; set; }
        internal void ResetObservation() { Observation = null; Count = 0; LastQualifyingUtc = default; }
    }
}
