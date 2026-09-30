using System.Buffers.Binary;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Domain.Forms;

namespace Husaynia.Application.Forms;

public sealed class FormSubmissionValidator(FormsOptions options)
{
    private readonly FormsOptions options =
        options ?? throw new ArgumentNullException(nameof(options));

    public FormSubmissionValidationResult Validate(
        FormSubmissionCommand command,
        FormDefinitionView definition)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<FormValidationError>();
        if (command.Fields.Count > options.MaximumFieldCount)
        {
            errors.Add(Error(string.Empty, "too_many_fields", "Too many fields were submitted."));
        }

        var normalizedInput = NormalizeInput(command.Fields, errors);
        var fieldsByKey = definition.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
        foreach (var suppliedKey in normalizedInput.Keys)
        {
            if (!fieldsByKey.ContainsKey(suppliedKey))
            {
                errors.Add(Error(
                    suppliedKey,
                    "unknown_field",
                    "The submitted field is not part of this form."));
            }
        }

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in definition.Fields.OrderBy(field => field.Order))
        {
            normalizedInput.TryGetValue(field.Key, out var value);
            ValidateField(field, value, values, errors);
        }

        if (!string.Equals(
                definition.ConsentVersion,
                NormalizeOptional(command.ConsentVersion),
                StringComparison.Ordinal))
        {
            errors.Add(Error(
                string.Empty,
                "consent_version_mismatch",
                "The form consent disclosure changed; review it and submit again."));
        }

        var totalLength = values.Sum(pair => pair.Key.Length + pair.Value.Length);
        if (totalLength > options.MaximumTotalValueLength)
        {
            errors.Add(Error(
                string.Empty,
                "payload_too_large",
                "The submitted field values exceed the permitted total size."));
        }

        return new FormSubmissionValidationResult(
            values,
            errors,
            FormPayloadCanonicalizer.ComputeHash(values, definition.ConsentVersion));
    }

    private Dictionary<string, string> NormalizeInput(
        IReadOnlyDictionary<string, string> fields,
        List<FormValidationError> errors)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in fields)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > 100 ||
                pair.Key.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            {
                errors.Add(Error(
                    string.Empty,
                    "invalid_field_key",
                    "A submitted field has an invalid key."));
                continue;
            }

            var value = (pair.Value ?? string.Empty).Trim().Normalize();
            if (value.Length > options.MaximumFieldValueLength)
            {
                errors.Add(Error(
                    pair.Key,
                    "field_too_long",
                    "The field exceeds its maximum permitted length."));
                continue;
            }

            normalized[pair.Key] = value;
        }

        return normalized;
    }

    private static void ValidateField(
        FormFieldView field,
        string? value,
        SortedDictionary<string, string> values,
        List<FormValidationError> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (field.Required)
            {
                errors.Add(Error(field.Key, "required", $"{field.Label} is required."));
            }

            return;
        }

        if (field.MinimumLength.HasValue && value.Length < field.MinimumLength.Value)
        {
            errors.Add(Error(
                field.Key,
                "too_short",
                $"{field.Label} is shorter than the permitted minimum."));
        }

        if (field.MaximumLength.HasValue && value.Length > field.MaximumLength.Value)
        {
            errors.Add(Error(
                field.Key,
                "too_long",
                $"{field.Label} exceeds the permitted maximum."));
        }

        switch (field.Kind)
        {
            case FormFieldKind.Email:
                ValidateEmail(field, value, errors);
                break;
            case FormFieldKind.Telephone:
                ValidateTelephone(field, value, errors);
                break;
            case FormFieldKind.Decimal:
                ValidateDecimal(field, value, errors);
                break;
            case FormFieldKind.Choice:
                if (!field.Choices.Contains(value, StringComparer.Ordinal))
                {
                    errors.Add(Error(
                        field.Key,
                        "invalid_choice",
                        $"{field.Label} contains an unsupported choice."));
                }

                break;
            case FormFieldKind.Consent:
                if (!bool.TryParse(value, out var consent) || !consent)
                {
                    errors.Add(Error(
                        field.Key,
                        "consent_required",
                        $"{field.Label} must be accepted."));
                }
                else
                {
                    value = bool.TrueString.ToLowerInvariant();
                }

                break;
            case FormFieldKind.Text:
            case FormFieldKind.TextArea:
                break;
            default:
                errors.Add(Error(
                    field.Key,
                    "unsupported_field_kind",
                    $"{field.Label} has an unsupported field kind."));
                break;
        }

        values[field.Key] = value;
    }

    private static void ValidateEmail(
        FormFieldView field,
        string value,
        List<FormValidationError> errors)
    {
        try
        {
            var address = new MailAddress(value);
            if (!string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            errors.Add(Error(
                field.Key,
                "invalid_email",
                $"{field.Label} must be a valid email address."));
        }
    }

    private static void ValidateTelephone(
        FormFieldView field,
        string value,
        List<FormValidationError> errors)
    {
        var digits = value.Count(char.IsAsciiDigit);
        if (digits is < 7 or > 20 ||
            value.Any(character =>
                !(char.IsAsciiDigit(character) ||
                    char.IsWhiteSpace(character) ||
                    character is '+' or '-' or '(' or ')' or '.')))
        {
            errors.Add(Error(
                field.Key,
                "invalid_telephone",
                $"{field.Label} must be a valid telephone number."));
        }
    }

    private static void ValidateDecimal(
        FormFieldView field,
        string value,
        List<FormValidationError> errors)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var number) ||
            field.MinimumValue.HasValue && number < field.MinimumValue.Value ||
            field.MaximumValue.HasValue && number > field.MaximumValue.Value)
        {
            errors.Add(Error(
                field.Key,
                "invalid_decimal",
                $"{field.Label} must be within the permitted numeric range."));
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Normalize();

    private static FormValidationError Error(string fieldKey, string code, string message) =>
        new(fieldKey, code, message);
}

public static class FormPayloadCanonicalizer
{
    public static byte[] ComputeHash(
        IReadOnlyDictionary<string, string> values,
        string? consentVersion)
    {
        ArgumentNullException.ThrowIfNull(values);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, pair.Key);
            Append(hash, pair.Value);
        }

        Append(hash, consentVersion ?? string.Empty);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.Normalize());
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
