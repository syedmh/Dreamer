using System.Net;
using System.Security.Cryptography;
using System.Text;
using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Identity;

namespace Husaynia.Web.Areas.Admin.Identity;

public sealed class IdentityAnonymousAdmission(
    IdentityModuleOptions options,
    IIdentityAnonymousRateLimiter rateLimiter,
    TimeProvider timeProvider)
{
    private static readonly byte[] UnknownAddress = Encoding.UTF8.GetBytes("unknown");
    private readonly byte[] fingerprintKey =
        options?.AnonymousRateLimit.FingerprintKey.ToArray()
        ?? throw new ArgumentNullException(nameof(options));

    public byte[] CreateClientFingerprint(IPAddress? remoteIpAddress)
    {
        var normalizedAddress = remoteIpAddress?.IsIPv4MappedToIPv6 == true
            ? remoteIpAddress.MapToIPv4()
            : remoteIpAddress;
        var input = normalizedAddress?.GetAddressBytes() ?? UnknownAddress;
        return HMACSHA256.HashData(fingerprintKey, input);
    }

    public async Task<IdentityAnonymousAdmissionDecision> AttemptAsync(
        HttpContext httpContext,
        string endpointFamily,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        try
        {
            var decision = await rateLimiter.AttemptAsync(
                    endpointFamily,
                    CreateClientFingerprint(httpContext.Connection.RemoteIpAddress),
                    cancellationToken)
                .ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                return IdentityAnonymousAdmissionDecision.Allowed;
            }

            var remaining = decision.WindowEndsAtUtc - timeProvider.GetUtcNow();
            var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            return new IdentityAnonymousAdmissionDecision(false, retryAfterSeconds, false);
        }
        catch (IdentityAnonymousRateLimitDependencyException)
        {
            return IdentityAnonymousAdmissionDecision.DependencyUnavailable;
        }
    }
}

public sealed record IdentityAnonymousAdmissionDecision(
    bool IsAllowed,
    int RetryAfterSeconds,
    bool IsDependencyUnavailable)
{
    internal static IdentityAnonymousAdmissionDecision Allowed { get; } = new(true, 0, false);

    internal static IdentityAnonymousAdmissionDecision DependencyUnavailable { get; } =
        new(false, 0, true);
}
