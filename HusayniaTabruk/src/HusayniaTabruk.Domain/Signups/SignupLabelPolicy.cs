using System.Text;

namespace HusayniaTabruk.Domain.Signups;

public enum SignupLabelValidationError
{
    None = 0,
    TooLarge = 1,
    UnsupportedContent = 2,
}

public readonly record struct SignupLabelValidationResult(
    string? Value,
    SignupLabelValidationError Error)
{
    public bool IsSuccess => Error == SignupLabelValidationError.None;
}

/// <summary>
/// Constrains the optional signup label to contract vocabulary that cannot carry arbitrary
/// participant names or direct-contact text. Generic words cannot prove real-world anonymity,
/// so the label remains optional and callers must not use it to encode identity.
/// </summary>
public static class SignupLabelPolicy
{
    private const string OpenApiWordPattern =
        "(?:Food|Preparation|Serving|Cleanup|Household|Team|Group)";

    public const string OpenApiPattern =
        "^(?:" + OpenApiWordPattern + "(?: " + OpenApiWordPattern
        + ")*)(?: [1-9][0-9]{0,2})?(?![\\s\\S])";

    public const string OpenApiDescription =
        "Optional non-identifying label. Only the generic ASCII words Food, Preparation, "
        + "Serving, Cleanup, Household, Team, and Group, separated by single spaces, plus an "
        + "optional final integer from 1 through 999, are accepted. Arbitrary text, names, email "
        + "addresses, phone numbers, and other direct-contact forms are rejected. Generic "
        + "vocabulary cannot establish real-world anonymity, so callers must not encode identity. "
        + "Maximum 80 Unicode scalar values and 320 UTF-8 bytes.";

    private static readonly Dictionary<string, string> CanonicalWords =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Food"] = "Food",
            ["Preparation"] = "Preparation",
            ["Serving"] = "Serving",
            ["Cleanup"] = "Cleanup",
            ["Household"] = "Household",
            ["Team"] = "Team",
            ["Group"] = "Group",
        };

    public static SignupLabelValidationResult Normalize(string? value)
    {
        if (value is null)
        {
            return new SignupLabelValidationResult(
                Value: null,
                SignupLabelValidationError.None);
        }

        string label = value;
        if (label.EnumerateRunes().Count() > Common.ApplicationLimits.MaximumSignupLabelUnicodeScalars
            || Encoding.UTF8.GetByteCount(label) > Common.ApplicationLimits.MaximumSignupLabelUtf8Bytes)
        {
            return new SignupLabelValidationResult(
                Value: null,
                SignupLabelValidationError.TooLarge);
        }

        string[] tokens = label.Split(' ', StringSplitOptions.None);
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrEmpty))
        {
            return Unsupported();
        }

        int wordCount = tokens.Length;
        string? numericSuffix = null;
        if (IsAsciiDigits(tokens[^1]))
        {
            string candidate = tokens[^1];
            if (candidate.Length > 3
                || candidate[0] == '0'
                || !int.TryParse(
                    candidate,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int number)
                || number is < 1 or > 999)
            {
                return Unsupported();
            }

            numericSuffix = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            wordCount--;
        }

        if (wordCount == 0)
        {
            return Unsupported();
        }

        string[] canonicalTokens = new string[wordCount];
        for (int index = 0; index < wordCount; index++)
        {
            if (!CanonicalWords.TryGetValue(tokens[index], out string? canonical))
            {
                return Unsupported();
            }

            canonicalTokens[index] = canonical;
        }

        string normalized = string.Join(' ', canonicalTokens);
        if (numericSuffix is not null)
        {
            normalized = normalized + " " + numericSuffix;
        }

        return new SignupLabelValidationResult(
            normalized,
            SignupLabelValidationError.None);
    }

    private static bool IsAsciiDigits(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    private static SignupLabelValidationResult Unsupported() =>
        new(
            Value: null,
            SignupLabelValidationError.UnsupportedContent);
}
