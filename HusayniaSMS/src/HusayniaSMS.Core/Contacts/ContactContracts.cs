namespace HusayniaSMS.Core.Contacts;

public enum ContactErrorCode
{
    NameRequired,
    NumberRequired,
    InvalidE164,
    DuplicateNumber,
    FormulaPrefixNotAllowed
}

public sealed record ContactInput(int ImportOrdinal, string? Name, string? Number);

public sealed record ContactDraft(string? Name, string? Number);

public sealed record ContactRow(
    int ImportOrdinal,
    string Name,
    string Number,
    IReadOnlyList<ContactErrorCode> Errors)
{
    public bool IsEligible => Errors.Count == 0;
}

public interface IPhoneNumberValidator
{
    bool IsValid(string? value);
}

public interface IContactRowValidator
{
    IReadOnlyList<ContactRow> Validate(IReadOnlyList<ContactInput> inputs);
}

public sealed record ContactDraftValidation(
    string Name,
    string Number,
    IReadOnlyList<ContactErrorCode> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public interface IContactDraftValidator
{
    ContactDraftValidation Validate(
        ContactDraft draft,
        IReadOnlyList<ContactRow> currentRows,
        int? editingOrdinal);
}
