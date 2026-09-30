using TCFUploader.BrowserAuth;

namespace TCFUploader.Cli;

internal static class TokenAcquirer
{
    internal static async Task<TokenReadResult> ReadAsync(
        bool browserLogin,
        Func<string?> readEnvironment,
        TextReader stdin,
        bool isInputRedirected,
        Func<TextWriter, IBrowserLogin> createBrowserLogin,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var ordinary = await TokenSource.ReadAsync(
            readEnvironment,
            stdin,
            isInputRedirected,
            cancellationToken);
        if (!browserLogin ||
            ordinary is TokenReadResult.Success ||
            ordinary is not TokenReadResult.Error { Code: "token_missing" or "token_empty" })
        {
            return ordinary;
        }

        var browser = await createBrowserLogin(output).AcquireAsync(cancellationToken);
        return browser switch
        {
            BrowserLoginResult.Success success => new TokenReadResult.Success(success.Token, success.UserId),
            BrowserLoginResult.Cancelled => new TokenReadResult.Error(
                "browser_login_cancelled",
                "Browser login was canceled."),
            BrowserLoginResult.Error error => new TokenReadResult.Error(error.Code, error.Message),
            _ => throw new InvalidOperationException("Unknown browser login result.")
        };
    }
}
