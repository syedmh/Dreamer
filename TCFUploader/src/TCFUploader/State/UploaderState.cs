using System.Text.Json.Serialization;

namespace TCFUploader.State;

internal sealed record UploaderState(
    int SchemaVersion,
    string EventId,
    string WatchedRoot,
    DateTime UpdatedUtc,
    Dictionary<string, UploadItemState> Items)
{
    [JsonInclude]
    internal long MutationSequence { get; init; }
}
