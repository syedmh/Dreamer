using System;
using System.Collections.Generic;

namespace TCFAnimation;

public readonly record struct FireworkPoint(float X, float Y);

public readonly record struct FireworkBurst(
    int Id,
    FireworkPoint Center,
    float AgeSeconds,
    float DurationSeconds,
    float MaximumRadius,
    float RotationRadians,
    int RayCount,
    int PaletteIndex,
    uint SparkSeed);

public sealed class FireworksSimulation
{
    public const int MaximumBurstCount = 8;
    public const int MinimumRayCount = 18;
    public const int MaximumRayCount = 26;

    private const float BurstDurationSeconds = 1.7f;
    private const double SpawnIntervalSeconds = 0.48;

    private readonly List<MutableBurst> _bursts = [];
    private uint _randomState;
    private double _elapsedSeconds;
    private double _nextSpawnSeconds;
    private int _nextId;

    public bool IsActive { get; private set; }

    public int ActiveBurstCount => _bursts.Count;

    public void Start(int seed)
    {
        _bursts.Clear();
        _elapsedSeconds = 0.0;
        _nextSpawnSeconds = SpawnIntervalSeconds;
        _nextId = 0;
        _randomState = unchecked((uint)seed);
        if (_randomState == 0)
        {
            _randomState = 0x9E3779B9u;
        }

        IsActive = true;
        SpawnBurst(0.30f);
        SpawnBurst(0.16f);
        SpawnBurst(0.02f);
    }

    public void Stop()
    {
        IsActive = false;
        _bursts.Clear();
        _elapsedSeconds = 0.0;
        _nextSpawnSeconds = 0.0;
    }

    public void Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }

        if (!IsActive)
        {
            return;
        }

        _elapsedSeconds += deltaSeconds;
        for (int index = _bursts.Count - 1; index >= 0; index--)
        {
            MutableBurst burst = _bursts[index];
            burst.AgeSeconds += (float)deltaSeconds;
            if (burst.AgeSeconds >= burst.DurationSeconds)
            {
                _bursts.RemoveAt(index);
            }
        }

        while (_elapsedSeconds >= _nextSpawnSeconds)
        {
            SpawnBurst(0.0f);
            _nextSpawnSeconds += SpawnIntervalSeconds;
        }
    }

    public IReadOnlyList<FireworkBurst> Snapshot()
    {
        FireworkBurst[] snapshot = new FireworkBurst[_bursts.Count];
        for (int index = 0; index < _bursts.Count; index++)
        {
            MutableBurst burst = _bursts[index];
            snapshot[index] = new FireworkBurst(
                burst.Id,
                burst.Center,
                burst.AgeSeconds,
                burst.DurationSeconds,
                burst.MaximumRadius,
                burst.RotationRadians,
                burst.RayCount,
                burst.PaletteIndex,
                burst.SparkSeed);
        }

        return snapshot;
    }

    private void SpawnBurst(float initialAgeSeconds)
    {
        if (_bursts.Count == MaximumBurstCount)
        {
            _bursts.RemoveAt(0);
        }

        bool centerZone = _nextId % 3 == 0;
        bool leftZone = NextUnit() < 0.5f;
        float x = centerZone
            ? Lerp(790.0f, 1130.0f, NextUnit())
            : (
                leftZone
                    ? Lerp(150.0f, 760.0f, NextUnit())
                    : Lerp(1160.0f, 1770.0f, NextUnit())
            );
        float y = Lerp(115.0f, 505.0f, NextUnit());
        _bursts.Add(
            new MutableBurst
            {
                Id = _nextId++,
                Center = new FireworkPoint(x, y),
                AgeSeconds = initialAgeSeconds,
                DurationSeconds = BurstDurationSeconds,
                MaximumRadius = Lerp(125.0f, 250.0f, NextUnit()),
                RotationRadians = NextUnit() * MathF.Tau,
                RayCount = MinimumRayCount
                    + (int)MathF.Floor(
                        NextUnit()
                        * (MaximumRayCount - MinimumRayCount + 1)),
                PaletteIndex = (int)MathF.Floor(NextUnit() * 6.0f) % 6,
                SparkSeed = NextUInt(),
            });
    }

    private uint NextUInt()
    {
        uint value = _randomState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        _randomState = value;
        return value;
    }

    private float NextUnit()
    {
        return (NextUInt() & 0x00FFFFFFu) / 16777216.0f;
    }

    private static float Lerp(float from, float to, float weight)
    {
        return from + (to - from) * weight;
    }

    private sealed class MutableBurst
    {
        public int Id { get; init; }

        public FireworkPoint Center { get; init; }

        public float AgeSeconds { get; set; }

        public float DurationSeconds { get; init; }

        public float MaximumRadius { get; init; }

        public float RotationRadians { get; init; }

        public int RayCount { get; init; }

        public int PaletteIndex { get; init; }

        public uint SparkSeed { get; init; }
    }
}
