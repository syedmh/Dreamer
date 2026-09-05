using HusayniaSMS.Core.Contacts;

namespace HusayniaSMS.WinForms.Presentation;

internal sealed class ContactDocumentState
{
    private ContactDocumentState(
        IReadOnlyList<ContactRow> rows,
        string? path,
        ContactCsvVersion? version,
        bool isDirty,
        int nextOrdinal)
    {
        Rows = Array.AsReadOnly(rows.ToArray());
        Path = path;
        Version = version;
        IsDirty = isDirty;
        NextOrdinal = nextOrdinal;
    }

    public IReadOnlyList<ContactRow> Rows { get; }
    public string? Path { get; }
    public ContactCsvVersion? Version { get; }
    public bool IsDirty { get; }
    public int NextOrdinal { get; }

    public static ContactDocumentState CreateUntitled() =>
        new(Array.Empty<ContactRow>(), null, null, isDirty: false, nextOrdinal: 1);

    public static ContactDocumentState FromLoaded(
        string fullPath,
        ContactCsvVersion version,
        IReadOnlyList<ContactRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(rows);

        var nextOrdinal = rows.Count == 0
            ? 1
            : checked(rows.Max(row => row.ImportOrdinal) + 1);
        return new(rows, fullPath, version, isDirty: false, nextOrdinal);
    }

    public ContactDocumentState Add(
        ContactDraftValidation draft,
        IContactRowValidator rowValidator)
    {
        ValidateMutationArguments(draft, rowValidator);
        var inputs = Rows
            .Select(ToInput)
            .Append(new ContactInput(NextOrdinal, draft.Name, draft.Number))
            .ToArray();
        return Mutated(
            rowValidator.Validate(inputs),
            checked(NextOrdinal + 1));
    }

    public ContactDocumentState Edit(
        int ordinal,
        ContactDraftValidation draft,
        IContactRowValidator rowValidator)
    {
        ValidateMutationArguments(draft, rowValidator);
        if (!Rows.Any(row => row.ImportOrdinal == ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                "The contact ordinal does not exist.");
        }

        var inputs = Rows.Select(row =>
                row.ImportOrdinal == ordinal
                    ? new ContactInput(ordinal, draft.Name, draft.Number)
                    : ToInput(row))
            .ToArray();
        return Mutated(rowValidator.Validate(inputs), NextOrdinal);
    }

    public ContactDocumentState Delete(
        IReadOnlySet<int> ordinals,
        IContactRowValidator rowValidator)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        ArgumentNullException.ThrowIfNull(rowValidator);

        var inputs = Rows
            .Where(row => !ordinals.Contains(row.ImportOrdinal))
            .Select(ToInput)
            .ToArray();
        if (inputs.Length == Rows.Count)
        {
            throw new ArgumentException(
                "At least one existing contact ordinal is required.",
                nameof(ordinals));
        }

        return Mutated(rowValidator.Validate(inputs), NextOrdinal);
    }

    public ContactDocumentState MarkSaved(
        string fullPath,
        ContactCsvVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(version);
        return new(Rows, fullPath, version, isDirty: false, NextOrdinal);
    }

    private ContactDocumentState Mutated(
        IReadOnlyList<ContactRow> rows,
        int nextOrdinal) =>
        new(rows, Path, Version, isDirty: true, nextOrdinal);

    private static ContactInput ToInput(ContactRow row) =>
        new(row.ImportOrdinal, row.Name, row.Number);

    private static void ValidateMutationArguments(
        ContactDraftValidation draft,
        IContactRowValidator rowValidator)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(rowValidator);
        if (!draft.IsValid)
        {
            throw new ArgumentException(
                "A contact mutation requires a valid draft.",
                nameof(draft));
        }
    }
}
