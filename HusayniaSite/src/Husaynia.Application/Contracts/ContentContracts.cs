namespace Husaynia.Application.Contracts;

public interface IContentReader
{
    Task<Result<ContentView, ContentError>> GetByPathAsync(string path, CancellationToken ct);

    Task<Result<RevisionView, ContentError>> GetPreviewAsync(
        ContentId id,
        RevisionId revisionId,
        PreviewToken token,
        CancellationToken ct);
}

public interface IContentEditor
{
    Task<Result<DraftReceipt, ContentError>> SaveDraftAsync(
        ContentDraft draft,
        RevisionId? expectedDraft,
        UserContext actor,
        CancellationToken ct);
}

public interface IPublisher
{
    Task<Result<PublishReceipt, PublishError>> PublishAsync(
        ContentId id,
        RevisionId expectedDraft,
        UserContext actor,
        CancellationToken ct);

    Task<Result<PublishReceipt, PublishError>> UnpublishAsync(
        ContentId id,
        RowVersion expectedVersion,
        UserContext actor,
        CancellationToken ct);

    Task<Result<PublishReceipt, PublishError>> RollbackAsync(
        ContentId id,
        RevisionId priorRevision,
        RowVersion expectedVersion,
        UserContext actor,
        CancellationToken ct);
}

public sealed record ContentView(
    ContentId Id,
    RevisionId RevisionId,
    string CanonicalPath,
    string Title,
    string BodyHtml);

public sealed record RevisionView(
    ContentId Id,
    RevisionId RevisionId,
    string Title,
    string BodyHtml,
    DateTimeOffset CreatedAtUtc);

public sealed record ContentDraft(
    ContentId Id,
    string CanonicalPath,
    string Title,
    string BodyHtml,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record DraftReceipt(ContentId Id, RevisionId RevisionId, RowVersion Version);

public sealed record PublishReceipt(
    ContentId Id,
    RevisionId PublishedRevisionId,
    RowVersion Version,
    DateTimeOffset OccurredAtUtc);
