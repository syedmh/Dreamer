using MindScene.Core.Common;
using MindScene.Core.Models;

namespace MindScene.Core.Interfaces;

public interface IRenderer
{
    /// <summary>Renders a single frame and returns it as raw BGRA pixel data.</summary>
    Task<Result<byte[]>> RenderFrameAsync(ResolvedScene scene, float timeSeconds, CancellationToken cancellationToken = default);

    /// <summary>Renders all frames to the output directory, one PNG per frame.</summary>
    Task<Result<string>> RenderToFramesAsync(
        ResolvedScene scene,
        string outputDirectory,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default);
}
