using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Domain.Dates;

public static class DateErrorCodes
{
    public const string InvalidDateInput = "invalid_date_input";
    public const string DuplicateHelpCategory = "duplicate_help_category";
    public const string DuplicateHelpNeed = "duplicate_help_need";
    public const string VersionExhausted = "version_exhausted";

    internal static DomainError Conflict(string code, string message) => DomainError.Conflict(code, message);
    internal static DomainError Validation(string code, string message) => DomainError.Validation(code, message);
}
