namespace Husaynia.Infrastructure.Identity;

public sealed class IdentityAnonymousRateLimitOptions
{
    public int PermitLimit { get; init; }

    public TimeSpan Window { get; init; }

    public TimeSpan Retention { get; init; }

    public byte[] FingerprintKey { get; init; } = [];
}

public sealed class IdentityAnonymousRateLimitDependencyException : Exception
{
    public IdentityAnonymousRateLimitDependencyException(Exception innerException)
        : base("The anonymous identity rate-limit dependency is unavailable.", innerException)
    {
    }
}
