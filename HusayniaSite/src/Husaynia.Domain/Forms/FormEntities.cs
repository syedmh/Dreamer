using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Husaynia.Domain.Forms;

public enum FormFieldKind
{
    Text = 0,
    Email = 1,
    Telephone = 2,
    TextArea = 3,
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "Decimal is the frozen form-field kind name.")]
    Decimal = 4,
    Choice = 5,
    Consent = 6,
}

public enum FormPatternKind
{
    None = 0,
    Email = 1,
    Telephone = 2,
}

public enum FormPrivacyClass
{
    Standard = 0,
    Contact = 1,
    Consent = 2,
}

public enum FormRetentionStatus
{
    Active = 0,
    Eligible = 1,
    Anonymized = 2,
}

public enum FormDeliveryAttemptOutcome
{
    Started = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
}

public sealed class FormDefinition
{
    private FormDefinition()
    {
    }

    public FormDefinition(string key, string title)
    {
        Id = Guid.NewGuid();
        Key = FormDomainRules.RequireMachineKey(key, 100, nameof(key));
        Title = FormDomainRules.RequireText(title, 200, nameof(title));
    }

    public Guid Id { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public Guid? PublishedVersionId { get; private set; }

    public bool IsEnabled { get; private set; }

    public void Publish(FormDefinitionVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.DefinitionId != Id)
        {
            throw new InvalidOperationException("Only a version owned by the definition can be published.");
        }

        PublishedVersionId = version.Id;
        IsEnabled = true;
    }

    public void Disable() => IsEnabled = false;

    public void Rename(string title) =>
        Title = FormDomainRules.RequireText(title, 200, nameof(title));
}

public sealed class FormDefinitionVersion
{
    private FormDefinitionVersion()
    {
    }

    public FormDefinitionVersion(
        Guid definitionId,
        int version,
        string destinationKey,
        string templateKey,
        string? consentVersion,
        DateTimeOffset createdAtUtc)
    {
        if (definitionId == Guid.Empty)
        {
            throw new ArgumentException("A definition identifier is required.", nameof(definitionId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        Id = Guid.NewGuid();
        DefinitionId = definitionId;
        Version = version;
        DestinationKey = FormDomainRules.RequireMachineKey(
            destinationKey,
            100,
            nameof(destinationKey));
        TemplateKey = FormDomainRules.RequireMachineKey(templateKey, 100, nameof(templateKey));
        ConsentVersion = FormDomainRules.OptionalText(consentVersion, 100, nameof(consentVersion));
        CreatedAtUtc = FormDomainRules.Utc(createdAtUtc);
    }

    public Guid Id { get; private set; }

    public Guid DefinitionId { get; private set; }

    public int Version { get; private set; }

    public string DestinationKey { get; private set; } = string.Empty;

    public string TemplateKey { get; private set; } = string.Empty;

    public string? ConsentVersion { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}

public sealed class FormField
{
    private FormField()
    {
    }

    public FormField(
        Guid definitionVersionId,
        string key,
        string label,
        FormFieldKind kind,
        bool required,
        int? minimumLength,
        int? maximumLength,
        decimal? minimumValue,
        decimal? maximumValue,
        FormPatternKind patternKind,
        IReadOnlyCollection<string> choices,
        int order,
        FormPrivacyClass privacyClass,
        DateTimeOffset createdAtUtc)
    {
        if (definitionVersionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A definition-version identifier is required.",
                nameof(definitionVersionId));
        }

        if (!Enum.IsDefined(kind) || !Enum.IsDefined(patternKind) || !Enum.IsDefined(privacyClass))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (order is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(order));
        }

        ValidateBounds(
            kind,
            minimumLength,
            maximumLength,
            minimumValue,
            maximumValue,
            patternKind,
            choices);

        Id = Guid.NewGuid();
        DefinitionVersionId = definitionVersionId;
        Key = FormDomainRules.RequireMachineKey(key, 100, nameof(key));
        if (FormDomainRules.IsProhibitedFieldKey(Key))
        {
            throw new ArgumentException(
                "Secret, payment, and attachment fields are unsupported.",
                nameof(key));
        }

        Label = FormDomainRules.RequireText(label, 200, nameof(label));
        Kind = kind;
        Required = required;
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        MinimumValue = minimumValue;
        MaximumValue = maximumValue;
        PatternKind = patternKind;
        ChoicesJson = SerializeChoices(choices);
        Order = order;
        PrivacyClass = privacyClass;
        CreatedAtUtc = FormDomainRules.Utc(createdAtUtc);
    }

    public Guid Id { get; private set; }

    public Guid DefinitionVersionId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Label { get; private set; } = string.Empty;

    public FormFieldKind Kind { get; private set; }

    public bool Required { get; private set; }

    public int? MinimumLength { get; private set; }

    public int? MaximumLength { get; private set; }

    public decimal? MinimumValue { get; private set; }

    public decimal? MaximumValue { get; private set; }

    public FormPatternKind PatternKind { get; private set; }

    public string ChoicesJson { get; private set; } = "[]";

    public int Order { get; private set; }

    public FormPrivacyClass PrivacyClass { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public IReadOnlyList<string> GetChoices() =>
        JsonSerializer.Deserialize<string[]>(ChoicesJson) ?? [];

    private static void ValidateBounds(
        FormFieldKind kind,
        int? minimumLength,
        int? maximumLength,
        decimal? minimumValue,
        decimal? maximumValue,
        FormPatternKind patternKind,
        IReadOnlyCollection<string> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        if (minimumLength is < 0 ||
            maximumLength is < 1 or > 4_000 ||
            minimumLength > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (minimumValue > maximumValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumValue));
        }

        if (kind == FormFieldKind.Choice)
        {
            if (choices.Count is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(choices));
            }
        }
        else if (choices.Count > 0)
        {
            throw new ArgumentException("Only choice fields may define choices.", nameof(choices));
        }

        if (kind != FormFieldKind.Decimal &&
            (minimumValue.HasValue || maximumValue.HasValue))
        {
            throw new ArgumentException(
                "Only decimal fields may define numeric bounds.",
                nameof(minimumValue));
        }

        if (patternKind == FormPatternKind.Email && kind != FormFieldKind.Email ||
            patternKind == FormPatternKind.Telephone && kind != FormFieldKind.Telephone)
        {
            throw new ArgumentException(
                "The pattern kind is incompatible with the field kind.",
                nameof(patternKind));
        }
    }

    private static string SerializeChoices(IReadOnlyCollection<string> choices)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(choices.Count);
        foreach (var choice in choices)
        {
            var item = FormDomainRules.RequireText(choice, 200, nameof(choices));
            if (!seen.Add(item))
            {
                throw new ArgumentException("Choice values must be unique.", nameof(choices));
            }

            normalized.Add(item);
        }

        return JsonSerializer.Serialize(normalized);
    }
}

public sealed class FormSubmission
{
    private FormSubmission()
    {
    }

    public FormSubmission(
        Guid definitionVersionId,
        ReadOnlySpan<byte> duplicateFingerprint,
        ReadOnlySpan<byte> canonicalPayloadHash,
        DateTimeOffset duplicateWindowStartUtc,
        DateTimeOffset acceptedAtUtc,
        DateTimeOffset retentionEligibleAtUtc,
        string? consentVersion)
    {
        if (definitionVersionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A definition-version identifier is required.",
                nameof(definitionVersionId));
        }

        if (duplicateFingerprint.Length != 32 || canonicalPayloadHash.Length != 32)
        {
            throw new ArgumentException("Fingerprints and hashes must contain exactly 32 bytes.");
        }

        var accepted = FormDomainRules.Utc(acceptedAtUtc);
        var retentionEligible = FormDomainRules.Utc(retentionEligibleAtUtc);
        if (retentionEligible <= accepted)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionEligibleAtUtc));
        }

        Id = Guid.NewGuid();
        DefinitionVersionId = definitionVersionId;
        DuplicateFingerprint = duplicateFingerprint.ToArray();
        CanonicalPayloadHash = canonicalPayloadHash.ToArray();
        DuplicateWindowStartUtc = FormDomainRules.Utc(duplicateWindowStartUtc);
        AcceptedAtUtc = accepted;
        RetentionEligibleAtUtc = retentionEligible;
        ConsentVersion = FormDomainRules.OptionalText(consentVersion, 100, nameof(consentVersion));
        RetentionStatus = FormRetentionStatus.Active;
    }

    public Guid Id { get; private set; }

    public Guid DefinitionVersionId { get; private set; }

    public byte[] DuplicateFingerprint { get; private set; } = [];

    public byte[] CanonicalPayloadHash { get; private set; } = [];

    public DateTimeOffset DuplicateWindowStartUtc { get; private set; }

    public DateTimeOffset AcceptedAtUtc { get; private set; }

    public DateTimeOffset RetentionEligibleAtUtc { get; private set; }

    public string? ConsentVersion { get; private set; }

    public FormRetentionStatus RetentionStatus { get; private set; }

    public bool HasLegalHold { get; private set; }

    public DateTimeOffset? AnonymizedAtUtc { get; private set; }

    public Guid? DeliveryJobInstanceId { get; private set; }

    public void AttachDeliveryJob(Guid jobInstanceId)
    {
        if (jobInstanceId == Guid.Empty || DeliveryJobInstanceId.HasValue)
        {
            throw new InvalidOperationException("A delivery job can be attached exactly once.");
        }

        DeliveryJobInstanceId = jobInstanceId;
    }

    public void MarkRetentionEligible()
    {
        if (RetentionStatus == FormRetentionStatus.Active)
        {
            RetentionStatus = FormRetentionStatus.Eligible;
        }
    }

    public void PlaceLegalHold() => HasLegalHold = true;

    public void ReleaseLegalHold() => HasLegalHold = false;

    public void Anonymize(DateTimeOffset now)
    {
        if (HasLegalHold)
        {
            throw new InvalidOperationException("A legal hold fences submission anonymization.");
        }

        if (RetentionStatus == FormRetentionStatus.Anonymized)
        {
            return;
        }

        RetentionStatus = FormRetentionStatus.Anonymized;
        AnonymizedAtUtc = FormDomainRules.Utc(now);
    }
}

public sealed class FormSubmissionValue
{
    private FormSubmissionValue()
    {
    }

    public FormSubmissionValue(
        Guid submissionId,
        Guid fieldId,
        string fieldKey,
        string value,
        FormPrivacyClass privacyClass,
        DateTimeOffset createdAtUtc)
    {
        if (submissionId == Guid.Empty || fieldId == Guid.Empty)
        {
            throw new ArgumentException("Submission and field identifiers are required.");
        }

        Id = Guid.NewGuid();
        SubmissionId = submissionId;
        FieldId = fieldId;
        FieldKey = FormDomainRules.RequireMachineKey(fieldKey, 100, nameof(fieldKey));
        Value = FormDomainRules.OptionalText(value, 4_000, nameof(value)) ?? string.Empty;
        PrivacyClass = privacyClass;
        CreatedAtUtc = FormDomainRules.Utc(createdAtUtc);
    }

    public Guid Id { get; private set; }

    public Guid SubmissionId { get; private set; }

    public Guid FieldId { get; private set; }

    public string FieldKey { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public FormPrivacyClass PrivacyClass { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void Anonymize() => Value = string.Empty;
}

public sealed class FormDeliveryAttempt
{
    private FormDeliveryAttempt()
    {
    }

    public FormDeliveryAttempt(
        Guid submissionId,
        Guid jobInstanceId,
        int attemptNumber,
        DateTimeOffset startedAtUtc)
    {
        if (submissionId == Guid.Empty || jobInstanceId == Guid.Empty)
        {
            throw new ArgumentException("Submission and job identifiers are required.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

        Id = Guid.NewGuid();
        SubmissionId = submissionId;
        JobInstanceId = jobInstanceId;
        AttemptNumber = attemptNumber;
        StartedAtUtc = FormDomainRules.Utc(startedAtUtc);
        Outcome = FormDeliveryAttemptOutcome.Started;
    }

    public Guid Id { get; private set; }

    public Guid SubmissionId { get; private set; }

    public Guid JobInstanceId { get; private set; }

    public int AttemptNumber { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public FormDeliveryAttemptOutcome Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public byte[]? ProviderReceiptHash { get; private set; }

    public void CompleteSuccess(ReadOnlySpan<byte> providerReceiptHash, DateTimeOffset completedAtUtc)
    {
        if (providerReceiptHash.Length != 32)
        {
            throw new ArgumentException(
                "The provider receipt hash must contain exactly 32 bytes.",
                nameof(providerReceiptHash));
        }

        Finish(FormDeliveryAttemptOutcome.Succeeded, completedAtUtc, null);
        ProviderReceiptHash = providerReceiptHash.ToArray();
    }

    public void CompleteFailure(string errorCode, DateTimeOffset completedAtUtc) =>
        Finish(
            FormDeliveryAttemptOutcome.Failed,
            completedAtUtc,
            FormDomainRules.RequireMachineCode(errorCode, 100, nameof(errorCode)));

    public void CompleteCancellation(DateTimeOffset completedAtUtc) =>
        Finish(FormDeliveryAttemptOutcome.Cancelled, completedAtUtc, "cancelled");

    private void Finish(
        FormDeliveryAttemptOutcome outcome,
        DateTimeOffset completedAtUtc,
        string? errorCode)
    {
        if (Outcome != FormDeliveryAttemptOutcome.Started)
        {
            throw new InvalidOperationException("A delivery attempt can be finalized exactly once.");
        }

        Outcome = outcome;
        CompletedAtUtc = FormDomainRules.Utc(completedAtUtc);
        ErrorCode = errorCode;
    }
}

public sealed class FormRateLimit
{
    private FormRateLimit()
    {
    }

    public long Id { get; private set; }

    public string FormKey { get; private set; } = string.Empty;

    public byte[] ClientFingerprint { get; private set; } = [];

    public DateTimeOffset WindowStartedAtUtc { get; private set; }

    public DateTimeOffset WindowEndsAtUtc { get; private set; }

    public int RequestCount { get; private set; }

    public DateTimeOffset RetainUntilUtc { get; private set; }
}

internal static class FormDomainRules
{
    private static readonly string[] ProhibitedFieldKeyFragments =
    [
        "password",
        "passwd",
        "secret",
        "token",
        "authorization",
        "apikey",
        "api_key",
        "card",
        "cvc",
        "cvv",
        "iban",
        "routing",
        "accountnumber",
        "payment",
        "attachment",
        "upload",
        "file",
    ];

    internal static string RequireMachineKey(string value, int maximumLength, string parameterName)
    {
        var normalized = RequireText(value, maximumLength, parameterName);
        if (normalized.Length < 3 ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "A bounded ASCII machine key is required.",
                parameterName);
        }

        return normalized;
    }

    internal static string RequireMachineCode(string value, int maximumLength, string parameterName) =>
        RequireMachineKey(value, maximumLength, parameterName);

    internal static bool IsProhibitedFieldKey(string key)
    {
        var normalized = new string(
            key.Where(char.IsAsciiLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        return ProhibitedFieldKeyFragments.Any(fragment =>
            normalized.Contains(
                fragment.Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal));
    }

    internal static string RequireText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        var normalized = value.Trim().Normalize();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return normalized;
    }

    internal static string? OptionalText(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RequireText(value, maximumLength, parameterName);
    }

    internal static DateTimeOffset Utc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
}
