using System.Text;

namespace HusayniaSMS.Core.Messaging;

public sealed record MessageValidationResult(
    bool IsValid,
    int CharacterCount,
    string? ErrorCode,
    string? ErrorMessage);

public interface IMessageValidator
{
    MessageValidationResult Validate(string message);
}

public sealed class MessageValidator : IMessageValidator
{
    public const int MaximumCharacters = 1600;

    public MessageValidationResult Validate(string message)
    {
        message ??= string.Empty;
        var count = message.EnumerateRunes().Count();

        if (string.IsNullOrWhiteSpace(message))
        {
            return new(false, count, "MessageRequired",
                "Message must contain at least one non-whitespace character and be no longer than 1,600 characters.");
        }

        if (count > MaximumCharacters)
        {
            return new(false, count, "MessageTooLong",
                "Message must contain 1 to 1,600 characters.");
        }

        return new(true, count, null, null);
    }
}
