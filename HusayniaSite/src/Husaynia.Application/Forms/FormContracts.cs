using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Domain.Forms;

namespace Husaynia.Application.Forms;

public enum FormDeliveryMode
{
    Disabled = 0,
    Pickup = 1,
}

public sealed class FormsOptions
{
    public const string SectionName = "Forms";

    public bool Enabled { get; init; }

    public byte[] FingerprintKey { get; init; } = [];

    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromMinutes(15);

    public int RateLimitPermitLimit { get; init; } = 10;

    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RateLimitRetention { get; init; } = TimeSpan.FromDays(1);

    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(90);

    public int MaximumRequestBytes { get; init; } = 64_000;

    public int MaximumFieldCount { get; init; } = 50;

    public int MaximumFieldValueLength { get; init; } = 4_000;

    public int MaximumTotalValueLength { get; init; } = 16_000;

    public FormDeliveryMode DeliveryMode { get; init; } = FormDeliveryMode.Disabled;

    public string PickupDirectory { get; init; } = string.Empty;

    public string PickupDestinationKey { get; init; } = string.Empty;
}

public sealed record FormFieldView(
    Guid Id,
    Guid DefinitionVersionId,
    string Key,
    string Label,
    FormFieldKind Kind,
    bool Required,
    int? MinimumLength,
    int? MaximumLength,
    decimal? MinimumValue,
    decimal? MaximumValue,
    FormPatternKind PatternKind,
    IReadOnlyList<string> Choices,
    int Order,
    FormPrivacyClass PrivacyClass);

public sealed record FormDefinitionView(
    Guid DefinitionId,
    string FormKey,
    string Title,
    Guid DefinitionVersionId,
    int Version,
    string? ConsentVersion,
    IReadOnlyList<FormFieldView> Fields,
    RowVersion StateRowVersion);

public sealed record FormValidationError(
    string FieldKey,
    string Code,
    string Message);

public sealed record FormSubmissionValidationResult(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<FormValidationError> Errors,
    byte[] CanonicalPayloadHash)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ValidatedFormSubmission(
    FormDefinitionView Definition,
    IReadOnlyDictionary<string, string> Values,
    byte[] CanonicalPayloadHash,
    byte[] DuplicateFingerprint,
    string CorrelationId,
    DateTimeOffset AcceptedAtUtc);

public sealed record FormRateLimitDecision(
    bool IsAllowed,
    DateTimeOffset WindowEndsAtUtc,
    int RequestCount,
    int PermitLimit);

public interface IActiveFormDefinitionReader
{
    Task<Result<FormDefinitionView, FormError>> GetActiveAsync(
        string formKey,
        CancellationToken cancellationToken);
}

public interface IFormSubmissionStore
{
    Task<Result<FormReceipt, FormError>> SubmitAsync(
        ValidatedFormSubmission submission,
        CancellationToken cancellationToken);
}

public interface IFormsRateLimiter
{
    Task<FormRateLimitDecision> AttemptAsync(
        string formKey,
        ReadOnlyMemory<byte> clientFingerprint,
        CancellationToken cancellationToken);
}

public sealed class FormsRateLimitDependencyException : Exception
{
    public FormsRateLimitDependencyException(Exception innerException)
        : base("The Forms rate-limit dependency is unavailable.", innerException)
    {
    }
}

public sealed record FormDeliveryMessage(
    Guid AttemptId,
    string DestinationKey,
    string TemplateKey,
    IReadOnlyDictionary<string, string> Values);

public interface IFormDeliveryStore
{
    Task<Result<FormDeliveryMessage, FormError>> BeginAttemptAsync(
        Guid submissionId,
        Guid definitionVersionId,
        JobExecutionContext context,
        CancellationToken cancellationToken);

    Task<Result<bool, FormError>> CompleteAttemptAsync(
        Guid attemptId,
        bool succeeded,
        string? errorCode,
        byte[]? providerReceiptHash,
        bool cancelled,
        CancellationToken cancellationToken);
}

public sealed record FormFieldDraft(
    string Key,
    string Label,
    FormFieldKind Kind,
    bool Required,
    int? MinimumLength,
    int? MaximumLength,
    decimal? MinimumValue,
    decimal? MaximumValue,
    FormPatternKind PatternKind,
    IReadOnlyCollection<string> Choices,
    int Order,
    FormPrivacyClass PrivacyClass);

public sealed record PublishFormDefinitionCommand(
    string FormKey,
    string Title,
    string DestinationKey,
    string TemplateKey,
    string? ConsentVersion,
    IReadOnlyCollection<FormFieldDraft> Fields,
    RowVersion ExpectedStateRowVersion);

public sealed record FormDefinitionPublicationReceipt(
    Guid DefinitionId,
    Guid DefinitionVersionId,
    int Version,
    DateTimeOffset PublishedAtUtc,
    RowVersion StateRowVersion);

public sealed record FormSubmissionSummary(
    Guid SubmissionId,
    string FormKey,
    int DefinitionVersion,
    DateTimeOffset AcceptedAtUtc,
    FormRetentionStatus RetentionStatus,
    bool HasLegalHold,
    Guid? DeliveryJobInstanceId,
    string DeliveryState,
    RowVersion StateRowVersion);

public enum FormRetentionAction
{
    MarkEligible = 0,
    PlaceLegalHold = 1,
    ReleaseLegalHold = 2,
    Anonymize = 3,
}

public sealed record FormRetentionCommand(
    Guid SubmissionId,
    FormRetentionAction Action,
    RowVersion ExpectedStateRowVersion);

public sealed record FormRetentionReceipt(
    Guid SubmissionId,
    FormRetentionStatus Status,
    bool HasLegalHold,
    DateTimeOffset? AnonymizedAtUtc,
    RowVersion StateRowVersion);

public interface IFormAdministration
{
    Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
        string formKey,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
        PublishFormDefinitionCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
        int take,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<bool, FormError>> RetryDeliveryAsync(
        Guid submissionId,
        string reason,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
        FormRetentionCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);
}

public interface IFormAdministrationStore
{
    Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
        string formKey,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
        PublishFormDefinitionCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
        int take,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<bool, FormError>> RetryDeliveryAsync(
        Guid submissionId,
        string reason,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
        FormRetentionCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);
}

public interface IFormsDeliveryJobCoordinator
{
    Task<Result<Guid, JobStoreError>> RegisterAsync(CancellationToken cancellationToken);
}
