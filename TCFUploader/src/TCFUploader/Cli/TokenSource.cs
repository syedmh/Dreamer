using TCFUploader.Configuration;

namespace TCFUploader.Cli;

internal abstract record TokenReadResult
{
    internal sealed record Success(string Token, string? UserId = null) : TokenReadResult;
    internal sealed record Error(string Code, string Message) : TokenReadResult;
}

internal static class TokenSource
{
    private const int MaxCharacters = 16 * 1024;

    internal static async Task<TokenReadResult> ReadAsync(
        Func<string?> readEnvironment,
        TextReader stdin,
        bool isInputRedirected,
        CancellationToken cancellationToken)
    {
        var environmentToken = readEnvironment();
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return Validate(environmentToken);
        }

        if (!isInputRedirected)
        {
            return new TokenReadResult.Error("token_missing",
                $"Set {UploaderConstants.TokenEnvironmentVariable} or redirect the token through standard input.");
        }

        var buffer = new char[MaxCharacters + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stdin.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
            if (count == 0)
            {
                break;
            }
            read += count;
        }

        if (read > MaxCharacters)
        {
            return new TokenReadResult.Error("token_too_long", "The redirected token exceeds 16 KiB.");
        }

        return Validate(new string(buffer, 0, read).Trim('\r', '\n'));
    }

    internal static TokenReadResult Validate(string token)
    {
        if (token.Length > MaxCharacters)
        {
            return new TokenReadResult.Error("token_too_long", "The bearer token exceeds 16 KiB.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new TokenReadResult.Error("token_empty", "The bearer token is empty.");
        }

        if (token.Any(character => char.IsControl(character)))
        {
            return new TokenReadResult.Error("token_invalid", "The bearer token contains invalid control characters.");
        }

        return new TokenReadResult.Success(token);
    }
}
