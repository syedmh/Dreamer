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
            var name = input.Name?.Trim() ?? string.Empty;
            var number = input.Number?.Trim() ?? string.Empty;
            var errors = new List<ContactErrorCode>();

            if (name.Length == 0)
            {
                errors.Add(ContactErrorCode.NameRequired);
            }

            if (number.Length == 0)
            {
                errors.Add(ContactErrorCode.NumberRequired);
            }
            else if (!phoneNumberValidator.IsValid(number))
            {
                errors.Add(ContactErrorCode.InvalidE164);
            }

            if (errors.Count == 0 && !seenNumbers.Add(number))
            {
                errors.Add(ContactErrorCode.DuplicateNumber);
            }

            rows.Add(new ContactRow(input.ImportOrdinal, name, number, errors.AsReadOnly()));
        }

        return rows.AsReadOnly();
    }
}
