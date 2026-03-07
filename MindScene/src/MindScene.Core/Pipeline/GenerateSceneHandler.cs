using MediatR;
using Microsoft.Extensions.Logging;
using MindScene.Core.Common;
using MindScene.Core.Interfaces;
using MindScene.Core.Models;

namespace MindScene.Core.Pipeline;

public class GenerateSceneHandler : IRequestHandler<GenerateSceneCommand, Result<string>>
{
    private readonly ISceneParser _parser;
    private readonly IDialogueProcessor _dialogue;
    private readonly IRenderer _renderer;
    private readonly IExporter _exporter;
    private readonly ILogger<GenerateSceneHandler> _logger;

    public GenerateSceneHandler(
        ISceneParser parser,
        IDialogueProcessor dialogue,
        IRenderer renderer,
        IExporter exporter,
        ILogger<GenerateSceneHandler> logger)
    {
        _parser = parser;
        _dialogue = dialogue;
        _renderer = renderer;
        _exporter = exporter;
        _logger = logger;
    }

    public async Task<Result<string>> Handle(
        GenerateSceneCommand request, CancellationToken cancellationToken)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "mindscene", Guid.NewGuid().ToString("N")[..8]);
        var framesDir = Path.Combine(workDir, "frames");
        var audioDir = Path.Combine(workDir, "audio");
        Directory.CreateDirectory(framesDir);
        Directory.CreateDirectory(audioDir);

        _logger.LogInformation("=== MindScene Pipeline Start ===");
        _logger.LogInformation("Work directory: {Dir}", workDir);

        // Step 1: Parse scene
        _logger.LogInformation("[1/4] Parsing scene with AI...");
        var parseResult = await _parser.ParseAsync(request.Input, cancellationToken);
        if (parseResult.IsFailure)
        {
            _logger.LogError("Scene parsing failed: {Error}", parseResult.Error);
            return Result.Fail<string>(parseResult.Error);
        }
        var scene = parseResult.Value;
        _logger.LogInformation("[1/4] Scene parsed: {Id}, {Duration:F1}s, {Actors} actors",
            scene.SceneId, scene.TotalDurationSeconds, scene.Actors.Count);

        // Step 2: Process dialogue
        _logger.LogInformation("[2/4] Processing dialogue ({Count} lines)...", scene.Dialogue.Count);
        var dialogueResult = await _dialogue.ProcessDialogueAsync(
            scene.Dialogue, request.Input.Actors, cancellationToken);
        if (dialogueResult.IsFailure)
        {
            _logger.LogWarning("Dialogue processing failed (continuing): {Error}", dialogueResult.Error);
        }
        else
        {
            scene = scene with { Dialogue = dialogueResult.Value };
            // Move audio files to the work audio dir
            foreach (var line in scene.Dialogue.Where(d => d.AudioClipPath is not null))
            {
                var destPath = Path.Combine(audioDir, Path.GetFileName(line.AudioClipPath!));
                if (File.Exists(line.AudioClipPath!) && !File.Exists(destPath))
                    File.Move(line.AudioClipPath!, destPath);
            }
        }

        // Step 3: Render frames
        _logger.LogInformation("[3/4] Rendering {Frames} frames...",
            (int)(scene.TotalDurationSeconds * scene.Fps));
        var renderProgress = new Progress<float>(p =>
        {
            if ((int)(p * 100) % 10 == 0)
                _logger.LogInformation("  Render progress: {Pct:F0}%", p * 100);
        });
        var renderResult = await _renderer.RenderToFramesAsync(
            scene, framesDir, renderProgress, cancellationToken);
        if (renderResult.IsFailure)
        {
            _logger.LogError("Rendering failed: {Error}", renderResult.Error);
            return Result.Fail<string>(renderResult.Error);
        }

        // Step 4: Export video
        _logger.LogInformation("[4/4] Exporting to {Path}...", request.OutputPath);
        var (w, h) = request.Input.Resolution.ToDimensions();
        var exportOptions = new ExportOptions
        {
            OutputPath = request.OutputPath,
            Format = ExportFormat.Mp4,
            Resolution = request.Input.Resolution,
            Fps = request.Input.Fps
        };
        var exportResult = await _exporter.ExportAsync(
            framesDir, audioDir, exportOptions, null, cancellationToken);

        if (exportResult.IsFailure)
        {
            _logger.LogError("Export failed: {Error}", exportResult.Error);
            return Result.Fail<string>(exportResult.Error);
        }

        _logger.LogInformation("=== MindScene Complete: {Path} ===", exportResult.Value);

        // Cleanup temp work dir
        try { Directory.Delete(workDir, recursive: true); }
        catch { /* non-critical */ }

        return Result.Ok(exportResult.Value);
    }
}
