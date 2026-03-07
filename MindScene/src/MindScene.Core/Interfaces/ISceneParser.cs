using MindScene.Core.Common;
using MindScene.Core.Models;

namespace MindScene.Core.Interfaces;

public interface ISceneParser
{
    Task<Result<ResolvedScene>> ParseAsync(SceneInput input, CancellationToken cancellationToken = default);
}
