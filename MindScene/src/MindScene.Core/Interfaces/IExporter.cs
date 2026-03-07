using MindScene.Core.Common;
using MindScene.Core.Models;

namespace MindScene.Core.Interfaces;

public interface IExporter
{
    Task<Result<string>> ExportAsync(
        string framesDirectory,
        string audioDirectory,
        ExportOptions options,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default);
}

public record ExportOptions
{
    public string OutputPath { get; init; } = "output.mp4";
    public ExportFormat Format { get; init; } = ExportFormat.Mp4;
    public ResolutionPreset Resolution { get; init; } = ResolutionPreset.Hd720p;
    public int Fps { get; init; } = 24;
    public int VideoBitratekbps { get; init; } = 4000;
    public int AudioBitratekbps { get; init; } = 128;
}
