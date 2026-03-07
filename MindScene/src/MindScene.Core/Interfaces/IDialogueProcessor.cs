using MindScene.Core.Common;
using MindScene.Core.Models;

namespace MindScene.Core.Interfaces;

public interface IDialogueProcessor
{
    /// <summary>Processes all dialogue lines: generates TTS audio and lip-sync data, assigns timecodes.</summary>
    Task<Result<List<DialogueLine>>> ProcessDialogueAsync(
        List<DialogueLine> lines,
        List<ActorDefinition> actors,
        CancellationToken cancellationToken = default);
}
