using System.Text.RegularExpressions;

namespace HusayniaSMS.Core.Contacts;

public sealed partial class E164PhoneNumberValidator : IPhoneNumberValidator
{
    public bool IsValid(string? value) =>
        value is not null && E164Regex().IsMatch(value.Trim());

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164Regex();
}

public sealed class ContactRowValidator(IPhoneNumberValidator phoneNumberValidator)
    : IContactRowValidator
{
    public IReadOnlyList<ContactRow> Validate(IReadOnlyList<ContactInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var seenNumbers = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ContactRow>(inputs.Count);

        foreach (var input in inputs)
        {
            var fields = ContactFieldValidation.Validate(
                input.Name,
                input.Number,
                phoneNumberValidator);
            var errors = fields.Errors.ToList();

            if (errors.Count == 0 && !seenNumbers.Add(fields.Number))
            {
                errors.Add(ContactErrorCode.DuplicateNumber);
            }

            rows.Add(new ContactRow(
                input.ImportOrdinal,
                fields.Name,
                fields.Number,
                errors.AsReadOnly()));
        }

        return rows.AsReadOnly();
    }
}

public sealed class ContactDraftValidator(IPhoneNumberValidator phoneNumberValidator)
    : IContactDraftValidator
{
    public ContactDraftValidation Validate(
        ContactDraft draft,
        IReadOnlyList<ContactRow> currentRows,
        int? editingOrdinal)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(currentRows);

        var fields = ContactFieldValidation.Validate(
            draft.Name,
            draft.Number,
            phoneNumberValidator);
        var errors = fields.Errors.ToList();
        var numberIsValid = !errors.Contains(ContactErrorCode.NumberRequired) &&
            !errors.Contains(ContactErrorCode.InvalidE164);
        if (numberIsValid &&
            currentRows.Any(row =>
                row.ImportOrdinal != editingOrdinal &&
                phoneNumberValidator.IsValid(row.Number) &&
                StringComparer.Ordinal.Equals(row.Number.Trim(), fields.Number)))
        {
            errors.Add(ContactErrorCode.DuplicateNumber);
        }

        return new(fields.Name, fields.Number, errors.AsReadOnly());
    }
}

public static class ContactValidationMessages
{
    public static string GetMessage(ContactErrorCode code) => code switch
    {
        ContactErrorCode.NameRequired => "Name is required.",
        ContactErrorCode.NumberRequired => "Number is required.",
        ContactErrorCode.InvalidE164 =>
            "Number must be in E.164 format, for example +15550100100.",
        ContactErrorCode.DuplicateNumber =>
            "Another contact already uses this number.",
        ContactErrorCode.FormulaPrefixNotAllowed =>
            "Name cannot begin with =, +, -, or @.",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown contact error.")
    };
}

internal static class ContactFieldValidation
{
    public static ContactDraftValidation Validate(
        string? nameValue,
        string? numberValue,
        IPhoneNumberValidator phoneNumberValidator)
    {
        ArgumentNullException.ThrowIfNull(phoneNumberValidator);

        var name = TrimControlWhitespace(nameValue);
        var number = TrimControlWhitespace(numberValue);
        var errors = new List<ContactErrorCode>();

        if (name.Length == 0)
        {
            errors.Add(ContactErrorCode.NameRequired);
        }
        else if (name[0] is '=' or '+' or '-' or '@')
        {
            errors.Add(ContactErrorCode.FormulaPrefixNotAllowed);
        }

        if (number.Length == 0)
        {
            errors.Add(ContactErrorCode.NumberRequired);
        }
        else if (!phoneNumberValidator.IsValid(number))
        {
            errors.Add(ContactErrorCode.InvalidE164);
        }

        return new(name, number, errors.AsReadOnly());
    }

    private static string TrimControlWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var start = 0;
        while (start < value.Length && IsControlOrWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length - 1;
        while (end >= start && IsControlOrWhitespace(value[end]))
        {
            end--;
        }

        return start == 0 && end == value.Length - 1
            ? value
            : value[start..(end + 1)];
    }

    private static bool IsControlOrWhitespace(char value) =>
        char.IsControl(value) || char.IsWhiteSpace(value);
}
