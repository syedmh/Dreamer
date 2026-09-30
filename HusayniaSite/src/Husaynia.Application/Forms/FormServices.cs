using System.Security.Cryptography;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Forms;
using Husaynia.Domain.Identity;

namespace Husaynia.Application.Forms;

public sealed class FormSubmissionService(
    IActiveFormDefinitionReader definitionReader,
    IFormSubmissionStore store,
    FormSubmissionValidator validator,
    FormsOptions options,
    ICorrelationContext correlation,
    TimeProvider timeProvider) : IFormSubmissionService
{
    private readonly IActiveFormDefinitionReader definitionReader =
        definitionReader ?? throw new ArgumentNullException(nameof(definitionReader));
    private readonly IFormSubmissionStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly FormSubmissionValidator validator =
        validator ?? throw new ArgumentNullException(nameof(validator));
    private readonly FormsOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ICorrelationContext correlation =
        correlation ?? throw new ArgumentNullException(nameof(correlation));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<FormReceipt, FormError>> SubmitAsync(
        FormSubmissionCommand command,
        RequestFingerprint fingerprint,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!options.Enabled)
        {
            return Failure<FormReceipt>(
                "forms_disabled",
                "Form submissions are not currently available.");
        }

        if (command.Fields is null)
        {
            return Failure<FormReceipt>(
                "validation_failed",
                "One or more submitted fields are invalid.");
        }

        if (!TryDecodeFingerprint(fingerprint, out var fingerprintBytes))
        {
            return Failure<FormReceipt>(
                "invalid_fingerprint",
                "The form request could not be accepted.");
        }

        if (!TryNormalizeFormKey(command.FormKey, out var formKey))
        {
            return Failure<FormReceipt>(
                "form_not_found",
                "The requested form is not available.");
        }

        var definition = await definitionReader.GetActiveAsync(formKey, ct).ConfigureAwait(false);
        if (definition.IsFailure)
        {
            return Failure<FormReceipt>(
                "form_not_found",
                "The requested form is not available.");
        }

        var validation = validator.Validate(command with { FormKey = formKey }, definition.Success);
        if (!validation.IsValid)
        {
            return Failure<FormReceipt>(
                "validation_failed",
                "One or more submitted fields are invalid.");
        }

        return await store.SubmitAsync(
                new ValidatedFormSubmission(
                    definition.Success,
                    validation.Values,
                    validation.CanonicalPayloadHash,
                    fingerprintBytes,
                    correlation.Current.CorrelationId,
                    timeProvider.GetUtcNow().ToUniversalTime()),
                ct)
            .ConfigureAwait(false);
    }

    private static bool TryDecodeFingerprint(
        RequestFingerprint fingerprint,
        out byte[] fingerprintBytes)
    {
        fingerprintBytes = [];
        if (string.IsNullOrWhiteSpace(fingerprint.Value))
        {
            return false;
        }

        try
        {
            fingerprintBytes = Convert.FromBase64String(fingerprint.Value);
            return fingerprintBytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryNormalizeFormKey(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length is >= 3 and <= 100 &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static Result<T, FormError> Failure<T>(string code, string message) =>
        Result.Fail<T, FormError>(new FormError(code, message));
}

public sealed class FormAdministrationService(
    IFormAdministrationStore store,
    IIdentityAuditFinalizer auditFinalizer,
    AdministrativeCapabilityAuthorizer authorizer) : IFormAdministration
{
    public const string DefinitionReadAction = "forms.definition.read";
    public const string DefinitionPublishAction = "forms.definition.publish";
    public const string SubmissionReadAction = "forms.submission.read";
    public const string DeliveryRetryAction = "forms.delivery.retry";
    public const string RetentionChangeAction = "forms.retention.change";

    private readonly IFormAdministrationStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IIdentityAuditFinalizer auditFinalizer =
        auditFinalizer ?? throw new ArgumentNullException(nameof(auditFinalizer));
    private readonly AdministrativeCapabilityAuthorizer authorizer =
        authorizer ?? throw new ArgumentNullException(nameof(authorizer));

    public async Task<Result<FormDefinitionView, FormError>> GetDefinitionAsync(
        string formKey,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        var audit = Descriptor(actor, DefinitionReadAction, "FormDefinition", formKey);
        var denial = Authorize(actor, CapabilityAccess.Read);
        if (denial is not null)
        {
            return await AuditDeniedAsync<FormDefinitionView>(audit, denial).ConfigureAwait(false);
        }

        if (!FormSubmissionService.TryNormalizeFormKey(formKey, out var normalized))
        {
            return await AuditValidationFailureAsync<FormDefinitionView>(
                audit,
                "invalid_form_key",
                "A valid form key is required.").ConfigureAwait(false);
        }

        return await store.GetDefinitionAsync(normalized, audit, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<FormDefinitionPublicationReceipt, FormError>> PublishAsync(
        PublishFormDefinitionCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var audit = Descriptor(actor, DefinitionPublishAction, "FormDefinition", command.FormKey);
        var denial = Authorize(actor, CapabilityAccess.Write);
        if (denial is not null)
        {
            return await AuditDeniedAsync<FormDefinitionPublicationReceipt>(audit, denial)
                .ConfigureAwait(false);
        }

        var validationError = ValidatePublication(command);
        if (validationError is not null)
        {
            return await AuditValidationFailureAsync<FormDefinitionPublicationReceipt>(
                audit,
                validationError.Code,
                validationError.Message).ConfigureAwait(false);
        }

        return await store.PublishAsync(command, audit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<FormSubmissionSummary>, FormError>> ReadSubmissionsAsync(
        int take,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        var audit = Descriptor(actor, SubmissionReadAction, "FormSubmission", "summaries");
        var denial = Authorize(actor, CapabilityAccess.Read);
        if (denial is not null)
        {
            return await AuditDeniedAsync<IReadOnlyList<FormSubmissionSummary>>(audit, denial)
                .ConfigureAwait(false);
        }

        if (take is < 1 or > 200)
        {
            return await AuditValidationFailureAsync<IReadOnlyList<FormSubmissionSummary>>(
                audit,
                "invalid_take",
                "Submission queries must request between 1 and 200 items.").ConfigureAwait(false);
        }

        return await store.ReadSubmissionsAsync(take, audit, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<bool, FormError>> RetryDeliveryAsync(
        Guid submissionId,
        string reason,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        var audit = Descriptor(
            actor,
            DeliveryRetryAction,
            "FormSubmission",
            submissionId.ToString("N"));
        var denial = Authorize(actor, CapabilityAccess.Write);
        if (denial is not null)
        {
            return await AuditDeniedAsync<bool>(audit, denial).ConfigureAwait(false);
        }

        if (submissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length > 1_000)
        {
            return await AuditValidationFailureAsync<bool>(
                audit,
                "invalid_retry_request",
                "A submission identifier and bounded reason are required.").ConfigureAwait(false);
        }

        return await store.RetryDeliveryAsync(
                submissionId,
                reason.Trim().Normalize(),
                audit,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<FormRetentionReceipt, FormError>> ChangeRetentionAsync(
        FormRetentionCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var audit = Descriptor(
            actor,
            RetentionChangeAction,
            "FormSubmission",
            command.SubmissionId.ToString("N"));
        var denial = Authorize(actor, CapabilityAccess.Write);
        if (denial is not null)
        {
            return await AuditDeniedAsync<FormRetentionReceipt>(audit, denial)
                .ConfigureAwait(false);
        }

        if (command.SubmissionId == Guid.Empty ||
            !Enum.IsDefined(command.Action) ||
            command.ExpectedStateRowVersion.Value.Length != 8)
        {
            return await AuditValidationFailureAsync<FormRetentionReceipt>(
                audit,
                "invalid_retention_request",
                "A valid retention request is required.").ConfigureAwait(false);
        }

        return await store.ChangeRetentionAsync(command, audit, cancellationToken)
            .ConfigureAwait(false);
    }

    private FormError? Authorize(
        AdministrativeRequestActor actor,
        CapabilityAccess access)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var evaluation = authorizer.Authorize(
            actor,
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            access,
            allowLimited: false);
        if (!evaluation.Allowed)
        {
            return new FormError(evaluation.ErrorCode, evaluation.Message);
        }

        return actor.Roles.Contains(RoleNames.SiteAdministrator)
            ? null
            : new FormError(
                "forbidden",
                "Only a site administrator can manage form submissions.");
    }

    private async Task<Result<T, FormError>> AuditDeniedAsync<T>(
        IdentityAuditDescriptor audit,
        FormError error)
    {
        await auditFinalizer.FinalizeOnceAsync(
            audit,
            PrivilegedAttemptOutcome.Denied,
            new Dictionary<string, string?>
            {
                ["errorCode"] = error.Code,
                ["result"] = "denied",
            }).ConfigureAwait(false);
        return Result.Fail<T, FormError>(error);
    }

    private async Task<Result<T, FormError>> AuditValidationFailureAsync<T>(
        IdentityAuditDescriptor audit,
        string code,
        string message)
    {
        await auditFinalizer.FinalizeOnceAsync(
            audit,
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = code,
                ["result"] = "invalid",
            }).ConfigureAwait(false);
        return Result.Fail<T, FormError>(new FormError(code, message));
    }

    private static IdentityAuditDescriptor Descriptor(
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string? targetId) =>
        new(
            actor.UserId,
            actor.Roles,
            action,
            targetType,
            NormalizeTargetId(targetId),
            actor.CorrelationId);

    private static string? NormalizeTargetId(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        var normalized = targetId.Trim();
        return normalized.Length <= 256
            ? normalized
            : "sha256:" + Convert.ToHexString(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

    private static FormError? ValidatePublication(PublishFormDefinitionCommand command)
    {
        if (!FormSubmissionService.TryNormalizeFormKey(command.FormKey, out _) ||
            string.IsNullOrWhiteSpace(command.Title) ||
            command.Title.Trim().Length > 200 ||
            !FormSubmissionService.TryNormalizeFormKey(command.DestinationKey, out _) ||
            !FormSubmissionService.TryNormalizeFormKey(command.TemplateKey, out _) ||
            command.ConsentVersion?.Trim().Length > 100 ||
            command.Fields is null ||
            command.Fields.Count is < 1 or > 100 ||
            command.ExpectedStateRowVersion.Value.Length is not (0 or 8))
        {
            return new FormError(
                "invalid_definition",
                "The form definition is invalid or exceeds configured bounds.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        foreach (var field in command.Fields)
        {
            if (field is null ||
                !FormSubmissionService.TryNormalizeFormKey(field.Key, out _) ||
                string.IsNullOrWhiteSpace(field.Label) ||
                field.Label.Trim().Length > 200 ||
                !Enum.IsDefined(field.Kind) ||
                !Enum.IsDefined(field.PatternKind) ||
                !Enum.IsDefined(field.PrivacyClass) ||
                !keys.Add(field.Key) ||
                !orders.Add(field.Order))
            {
                return new FormError(
                    "invalid_field_definition",
                    "A form field definition is invalid or duplicated.");
            }

            try
            {
                _ = new FormField(
                    Guid.NewGuid(),
                    field.Key,
                    field.Label,
                    field.Kind,
                    field.Required,
                    field.MinimumLength,
                    field.MaximumLength,
                    field.MinimumValue,
                    field.MaximumValue,
                    field.PatternKind,
                    field.Choices,
                    field.Order,
                    field.PrivacyClass,
                    DateTimeOffset.UnixEpoch);
            }
            catch (ArgumentException)
            {
                return new FormError(
                    "invalid_field_definition",
                    "A form field definition is invalid or exceeds configured bounds.");
            }
        }

        return null;
    }
}
