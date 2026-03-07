using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Logging;
using MindScene.Core.Common;
using MindScene.Core.Interfaces;
using MindScene.Core.Models;

namespace MindScene.Export;

public class FfmpegExporter : IExporter
{
    private readonly ILogger<FfmpegExporter> _logger;

    public FfmpegExporter(ILogger<FfmpegExporter> logger) => _logger = logger;

    public async Task<Result<string>> ExportAsync(
        string framesDirectory,
        string audioDirectory,
        ExportOptions options,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
            var audioFiles = Directory.GetFiles(audioDirectory, "*.wav")
                .OrderBy(f => f)
                .ToList();

            _logger.LogInformation("Exporting {Format} to {Path}", options.Format, options.OutputPath);

            if (options.Format == ExportFormat.Gif)
                return await ExportGifAsync(framesDirectory, options, cancellationToken);

            string? mixedAudioPath = null;
            if (audioFiles.Count > 0)
            {
                mixedAudioPath = Path.Combine(audioDirectory, "mixed.wav");
                var mixResult = await MixAudioAsync(audioFiles, mixedAudioPath, cancellationToken);
                if (mixResult.IsFailure)
                    _logger.LogWarning("Audio mix failed (will export silent): {Error}", mixResult.Error);
                else if (!mixResult.Value) mixedAudioPath = null;
            }

            return await BuildVideoAsync(framesDirectory, mixedAudioPath, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            return Result.Fail<string>($"Export failed: {ex.Message}");
        }
    }

    private async Task<Result<string>> BuildVideoAsync(
        string framesDir, string? audioPath, ExportOptions options, CancellationToken ct)
    {
        var inputPattern = Path.Combine(framesDir, "frame_%06d.png");
        var (w, h) = options.Resolution.ToDimensions();

        var hasAudio = audioPath is not null && File.Exists(audioPath);

        var builder = FFMpegArguments
            .FromFileInput(inputPattern, false, o => o
                .WithFramerate(options.Fps)
                .WithCustomArgument("-pattern_type sequence"));

        if (hasAudio)
            builder = builder.AddFileInput(audioPath!);

        var processor = builder.OutputToFile(options.OutputPath, true, o =>
        {
            o.WithVideoCodec(VideoCodec.LibX264)
             .WithConstantRateFactor(23)
             .WithVideoBitrate(options.VideoBitratekbps)
             .WithFramerate(options.Fps)
             .WithCustomArgument("-pix_fmt yuv420p")
             .WithCustomArgument($"-vf scale={w}:{h}");

            if (hasAudio)
                o.WithAudioCodec(AudioCodec.Aac)
                 .WithAudioBitrate(options.AudioBitratekbps);
            else
                o.DisableChannel(Channel.Audio);
        });

        bool success = await processor.ProcessAsynchronously(true, new FFOptions
        {
            BinaryFolder = GetFfmpegBinaryFolder()
        });

        if (!success)
            return Result.Fail<string>("FFmpeg process returned failure");

        _logger.LogInformation("Export complete: {Path}", options.OutputPath);
        return Result.Ok(options.OutputPath);
    }

    private async Task<Result<bool>> MixAudioAsync(
        List<string> audioFiles, string outputPath, CancellationToken ct)
    {
        if (audioFiles.Count == 1)
        {
            File.Copy(audioFiles[0], outputPath, overwrite: true);
            return Result.Ok(true);
        }

        try
        {
            // Use sox-style amix via ffmpeg if multiple audio files
            var inputs = string.Join(" ", audioFiles.Select(f => $"-i \"{f}\""));
            var args = $"{inputs} -filter_complex amix=inputs={audioFiles.Count}:duration=longest \"{outputPath}\"";

            bool success = await FFMpegArguments
                .FromPipeInput(null!)
                .OutputToFile(outputPath)
                .ProcessAsynchronously();

            return Result.Ok(success);
        }
        catch (Exception ex)
        {
            return Result.Fail<bool>($"Audio mix error: {ex.Message}");
        }
    }

    private async Task<Result<string>> ExportGifAsync(
        string framesDir, ExportOptions options, CancellationToken ct)
    {
        var inputPattern = Path.Combine(framesDir, "frame_%06d.png");
        bool success = await FFMpegArguments
            .FromFileInput(inputPattern, false, o => o
                .WithFramerate(options.Fps)
                .WithCustomArgument("-pattern_type sequence"))
            .OutputToFile(options.OutputPath, true, o => o
                .WithFramerate(15)
                .WithCustomArgument("-vf scale=480:-1:flags=lanczos,fps=15,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"))
            .ProcessAsynchronously(true, new FFOptions { BinaryFolder = GetFfmpegBinaryFolder() });

        return success ? Result.Ok(options.OutputPath) : Result.Fail<string>("GIF export failed");
    }

    private static string GetFfmpegBinaryFolder()
    {
        // Try common locations
        var envPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) return envPath;

        // Default: expect ffmpeg in PATH
        return string.Empty;
    }
}
