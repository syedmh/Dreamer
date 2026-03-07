using MediatR;
using MindScene.Core.Common;
using MindScene.Core.Models;

namespace MindScene.Core.Pipeline;

/// <summary>MediatR command to kick off the full scene generation pipeline.</summary>
public record GenerateSceneCommand(SceneInput Input, string OutputPath) : IRequest<Result<string>>;
