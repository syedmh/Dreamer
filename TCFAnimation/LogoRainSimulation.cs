using System;
using System.Collections.Generic;

namespace TCFAnimation;

public readonly record struct LogoDrop(
    float X,
    float Y,
    float FallSpeed,
    float DriftSpeed,
    float RotationRadians,
    float AngularVelocity,
    float Scale);

public sealed class LogoRainSimulation
{
    public static double DurationSeconds =>
        AnimationConfig.Current.LogoRainSpawnSeconds;
    public const int MaximumDropCount = 72;

    private static double SpawnIntervalSeconds =>
        AnimationConfig.Current.LogoRainSpawnIntervalSeconds;
    private const double Epsilon = 1e-9;
    private const float ViewportWidth = 1920.0f;
    private const float ViewportHeight = 1080.0f;
    private const float RemovalMargin = 120.0f;

    private readonly List<LogoDrop> _drops = [];
    private uint _randomState;
    private double _elapsedSeconds;
    private double _nextSpawnSeconds;
    private int _nextDropIndex;
    private bool _isSpawning;

    public bool IsActive { get; private set; }

    public bool IsSpawning => IsActive && _isSpawning;

    public double ElapsedSeconds => _elapsedSeconds;

    public int ActiveDropCount => _drops.Count;

    public void Start(int seed)
    {
        _drops.Clear();
        _elapsedSeconds = 0.0;
        _nextSpawnSeconds = SpawnIntervalSeconds;
        _nextDropIndex = 0;
        _randomState = unchecked((uint)seed);
        if (_randomState == 0)
        {
            _randomState = 0x9E3779B9u;
        }

        IsActive = true;
        _isSpawning = true;
        for (int index = 0; index < 14; index++)
        {
            SpawnDrop(
                index == 0
                    ? 160.0f
                    : -NextUnit() * ViewportHeight * 0.75f);
        }
    }

    public void Stop()
    {
        IsActive = false;
        _isSpawning = false;
        _drops.Clear();
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

        for (int index = _drops.Count - 1; index >= 0; index--)
        {
            LogoDrop drop = _drops[index];
            float delta = (float)deltaSeconds;
            drop = drop with
            {
                X = WrapX(drop.X + drop.DriftSpeed * delta),
                Y = drop.Y + drop.FallSpeed * delta,
                RotationRadians =
                    drop.RotationRadians + drop.AngularVelocity * delta,
            };
            if (drop.Y > ViewportHeight + RemovalMargin)
            {
                _drops.RemoveAt(index);
            }
            else
            {
                _drops[index] = drop;
            }
        }

        double spawnThrough = Math.Min(
            _elapsedSeconds,
            DurationSeconds);
        while (
            _isSpawning
            && _nextSpawnSeconds + Epsilon < DurationSeconds
            && spawnThrough + Epsilon >= _nextSpawnSeconds
        )
        {
            SpawnDrop(-RemovalMargin);
            _nextSpawnSeconds += SpawnIntervalSeconds;
        }

        if (_elapsedSeconds + Epsilon >= DurationSeconds)
        {
            _isSpawning = false;
        }

        if (!_isSpawning && _drops.Count == 0)
        {
            IsActive = false;
        }
    }

    public IReadOnlyList<LogoDrop> Snapshot()
    {
        return _drops.ToArray();
    }

    private void SpawnDrop(float y)
    {
        if (_drops.Count == MaximumDropCount)
        {
            _drops.RemoveAt(0);
        }

        bool centerPath = _nextDropIndex++ % 3 == 0;
        float x = centerPath
            ? Lerp(820.0f, 1100.0f, NextUnit())
            : Lerp(50.0f, ViewportWidth - 50.0f, NextUnit());
        _drops.Add(
            new LogoDrop(
                x,
                y,
                Lerp(145.0f, 290.0f, NextUnit()),
                Lerp(-42.0f, 42.0f, NextUnit()),
                NextUnit() * MathF.Tau,
                Lerp(-1.25f, 1.25f, NextUnit()),
                Lerp(0.62f, 1.0f, NextUnit())));
    }

    private static float WrapX(float x)
    {
        if (x < -RemovalMargin)
        {
            return ViewportWidth + RemovalMargin;
        }

        if (x > ViewportWidth + RemovalMargin)
        {
            return -RemovalMargin;
        }

        return x;
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
}
