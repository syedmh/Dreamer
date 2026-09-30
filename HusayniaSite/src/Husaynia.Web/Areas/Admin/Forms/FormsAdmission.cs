using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;

namespace Husaynia.Web.Areas.Admin.Forms;

public sealed class FormsAdmission(
    FormsOptions options,
    IFormsRateLimiter rateLimiter,
    TimeProvider timeProvider)
{
    private static readonly byte[] UnknownAddress = Encoding.UTF8.GetBytes("unknown");
    private readonly FormsOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly IFormsRateLimiter rateLimiter =
        rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public byte[] CreateRateLimitFingerprint(IPAddress? remoteAddress, string formKey) =>
        Compute(
            "rate",
            NormalizeAddress(remoteAddress),
            Encoding.UTF8.GetBytes(formKey));

    public RequestFingerprint CreateDuplicateFingerprint(
        IPAddress? remoteAddress,
        string formKey,
        ReadOnlySpan<byte> canonicalPayloadHash)
    {
        if (canonicalPayloadHash.Length != 32)
        {
            throw new ArgumentException(
                "The canonical payload hash must contain exactly 32 bytes.",
                nameof(canonicalPayloadHash));
        }

        var value = Compute(
            "duplicate",
            NormalizeAddress(remoteAddress),
            Encoding.UTF8.GetBytes(formKey),
            canonicalPayloadHash.ToArray());
        return new RequestFingerprint(Convert.ToBase64String(value));
    }

    public async Task<FormsAdmissionDecision> AttemptAsync(
        HttpContext httpContext,
        string formKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        try
        {
            var decision = await rateLimiter.AttemptAsync(
                    formKey,
                    CreateRateLimitFingerprint(
                        httpContext.Connection.RemoteIpAddress,
                        formKey),
                    cancellationToken)
                .ConfigureAwait(false);
            if (decision.IsAllowed)
            {
                return FormsAdmissionDecision.Allowed;
            }

            var remaining = decision.WindowEndsAtUtc - timeProvider.GetUtcNow();
            return new FormsAdmissionDecision(
                false,
                Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)),
                false);
        }
        catch (FormsRateLimitDependencyException)
        {
            return FormsAdmissionDecision.DependencyUnavailable;
        }
    }

    private byte[] Compute(string purpose, params byte[][] parts)
    {
        using var hmac = IncrementalHash.CreateHMAC(
            HashAlgorithmName.SHA256,
            options.FingerprintKey);
        Append(hmac, Encoding.UTF8.GetBytes(purpose));
        foreach (var part in parts)
        {
            Append(hmac, part);
        }

        return hmac.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static byte[] NormalizeAddress(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return UnknownAddress;
        }

        return remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4().GetAddressBytes()
            : remoteAddress.GetAddressBytes();
    }
}

public sealed record FormsAdmissionDecision(
    bool IsAllowed,
    int RetryAfterSeconds,
    bool IsDependencyUnavailable)
{
    internal static FormsAdmissionDecision Allowed { get; } = new(true, 0, false);

    internal static FormsAdmissionDecision DependencyUnavailable { get; } =
        new(false, 0, true);
}
