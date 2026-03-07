using MindScene.Core.Common;

namespace MindScene.Core.Interfaces;

public interface IAiClient
{
    Task<Result<string>> CompleteAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default);

    Task<Result<T>> CompleteJsonAsync<T>(
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default);
}
