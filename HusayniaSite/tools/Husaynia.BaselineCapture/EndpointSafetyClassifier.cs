namespace Husaynia.BaselineCapture;

internal static class EndpointSafetyClassifier
{
    private static readonly string[] BlockedPathFragments =
    [
        "/wp-admin/",
        "admin-ajax.php",
        "/wp-login",
        "/wp-json/give",
        "/wp-json/contact-form-7",
        "/contact-form-7",
        "/wpcf7",
        "/wpforms",
        "/register",
        "/signup",
        "/sign-up",
        "/oauth",
        "/authorize",
        "/password-reset",
        "/reset-password",
        "/lost-password",
        "/forgot-password",
        "/login",
        "/account",
        "/my-account",
        "/checkout",
        "/payment",
        "/payments",
        "/pay/",
        "/cart",
        "stripe",
        "paypal"
    ];

    public static bool IsSensitiveEndpoint(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var value = Uri.UnescapeDataString($"{uri.AbsolutePath}{uri.Query}");
        return BlockedPathFragments.Any(fragment =>
            value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
