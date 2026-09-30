using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Domain.Threads;

public static class ThreadErrorCodes
{
    public const string InvalidThreadState = "invalid_thread_state";
    public const string ThreadAccessDenied = "thread_access_denied";
    public const string MessageNotOwned = "message_not_owned";
    public const string MessageAlreadyHidden = "message_already_hidden";
    public const string DuplicateClientMessage = "duplicate_client_message";
    public const string InvalidMessageContent = "invalid_message_content";
    public const string InvalidReport = "invalid_report";

    public static DomainError Validation(string code, string message) =>
        DomainError.Validation(code, message);

    public static DomainError Conflict(string code, string message) =>
        DomainError.Conflict(code, message);

    public static DomainError Concealed() =>
        DomainError.NotFound(
            ThreadAccessDenied,
            "The thread was not found.");
}
