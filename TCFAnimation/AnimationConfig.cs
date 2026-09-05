using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCFAnimation;

public sealed record AnimationConfig
{
    public double TurnFps { get; init; }
    public double WalkFps { get; init; }
    public double ClapFps { get; init; }
    public double CrossArmFps { get; init; }
    public double CrossArmReleaseFps { get; init; }
    public double SchoolPrepositionSeconds { get; init; }
    public double SchoolEntrySeconds { get; init; }
    public double SchoolClapSeconds { get; init; }
    public double SchoolBackgroundNormalizationSeconds { get; init; }
    public double SchoolExitSeconds { get; init; }
    public double CelebrationWalkSeconds { get; init; }
    public double CelebrationClapSeconds { get; init; }
    public double FireworksSpawnIntervalSeconds { get; init; }
    public double FireworksBurstSeconds { get; init; }
    public double LogoRainSpawnSeconds { get; init; }
    public double LogoRainSpawnIntervalSeconds { get; init; }

    public static AnimationConfig Defaults { get; } = new()
    {
        TurnFps = 8.0,
        WalkFps = 6.0,
        ClapFps = 8.0,
        CrossArmFps = 8.0,
        CrossArmReleaseFps = 8.0,
        SchoolPrepositionSeconds = 6.0,
        SchoolEntrySeconds = 8.0,
        SchoolClapSeconds = 10.0,
        SchoolBackgroundNormalizationSeconds = 8.0,
        SchoolExitSeconds = 8.0,
        CelebrationWalkSeconds = 6.0,
        CelebrationClapSeconds = 30.0,
        FireworksSpawnIntervalSeconds = 0.48,
        FireworksBurstSeconds = 1.7,
        LogoRainSpawnSeconds = 10.0,
        LogoRainSpawnIntervalSeconds = 0.12,
    };

    public static AnimationConfig Current { get; private set; } = Defaults;

    public static AnimationConfig Load(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            AnimationConfig config =
                JsonSerializer.Deserialize<AnimationConfig>(
                    utf8Json,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        UnmappedMemberHandling =
                            JsonUnmappedMemberHandling.Disallow,
                    })
                ?? throw new InvalidDataException(
                    "AnimationConfig.json must contain an object.");
            config.Validate();
            return config;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "AnimationConfig.json is invalid.",
                exception);
        }
    }

    public static void Install(AnimationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        Current = config;
    }

    private void Validate()
    {
        foreach (
            (string name, double value) in new[]
            {
                (nameof(TurnFps), TurnFps),
                (nameof(WalkFps), WalkFps),
                (nameof(ClapFps), ClapFps),
                (nameof(CrossArmFps), CrossArmFps),
                (nameof(CrossArmReleaseFps), CrossArmReleaseFps),
                (nameof(SchoolPrepositionSeconds), SchoolPrepositionSeconds),
                (nameof(SchoolEntrySeconds), SchoolEntrySeconds),
                (nameof(SchoolClapSeconds), SchoolClapSeconds),
                (
                    nameof(SchoolBackgroundNormalizationSeconds),
                    SchoolBackgroundNormalizationSeconds
                ),
                (nameof(SchoolExitSeconds), SchoolExitSeconds),
                (nameof(CelebrationWalkSeconds), CelebrationWalkSeconds),
                (nameof(CelebrationClapSeconds), CelebrationClapSeconds),
                (
                    nameof(FireworksSpawnIntervalSeconds),
                    FireworksSpawnIntervalSeconds
                ),
                (nameof(FireworksBurstSeconds), FireworksBurstSeconds),
                (nameof(LogoRainSpawnSeconds), LogoRainSpawnSeconds),
                (
                    nameof(LogoRainSpawnIntervalSeconds),
                    LogoRainSpawnIntervalSeconds
                ),
            })
        {
            if (!double.IsFinite(value) || value <= 0.0)
            {
                throw new InvalidDataException(
                    $"{name} must be a finite positive number.");
            }
        }
    }
}
