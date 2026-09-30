using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.Extensions.Options;

namespace HusayniaTabruk.Infrastructure.Identity.Services;

public interface IAccessTokenCodec
{
    string Issue(AccessTokenPayload payload);

    bool TryRead(
        string token,
        DateTimeOffset now,
        out AccessTokenPayload? payload);
}

public sealed record AccessTokenPayload(
    UserId UserId,
    MembershipId MembershipId,
    OrganizationId OrganizationId,
    DeviceId DeviceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed class HmacJwtAccessTokenCodec(IOptions<TabrukAuthOptions> options) : IAccessTokenCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Issue(AccessTokenPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        TabrukAuthOptions authOptions = options.Value;
        byte[] signingKey = authOptions.GetSigningKeyBytes();
        JwtHeader header = new("HS256", "JWT");
        JwtPayload body = new(
            authOptions.Issuer,
            authOptions.Audience,
            payload.UserId.Value.ToString("N"),
            payload.MembershipId.Value.ToString("N"),
            payload.OrganizationId.Value.ToString("N"),
            payload.DeviceId.Value.ToString("N"),
            payload.IssuedAt.ToUnixTimeSeconds(),
            payload.ExpiresAt.ToUnixTimeSeconds());

        string encodedHeader = OpaqueTokenCrypto.Base64UrlEncode(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header, SerializerOptions)));
        string encodedPayload = OpaqueTokenCrypto.Base64UrlEncode(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, SerializerOptions)));
        byte[] unsigned = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        using HMACSHA256 hmac = new(signingKey);
        string encodedSignature = OpaqueTokenCrypto.Base64UrlEncode(hmac.ComputeHash(unsigned));
        return $"{encodedHeader}.{encodedPayload}.{encodedSignature}";
    }

    public bool TryRead(
        string token,
        DateTimeOffset now,
        out AccessTokenPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        string[] segments = token.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        try
        {
            TabrukAuthOptions authOptions = options.Value;
            byte[] signingKey = authOptions.GetSigningKeyBytes();
            byte[] unsigned = Encoding.ASCII.GetBytes($"{segments[0]}.{segments[1]}");
            using HMACSHA256 hmac = new(signingKey);
            string expectedSignature = OpaqueTokenCrypto.Base64UrlEncode(hmac.ComputeHash(unsigned));
            if (!OpaqueTokenCrypto.FixedTimeEquals(expectedSignature, segments[2]))
            {
                return false;
            }

            JwtHeader? header = JsonSerializer.Deserialize<JwtHeader>(
                Encoding.UTF8.GetString(OpaqueTokenCrypto.Base64UrlDecode(segments[0])),
                SerializerOptions);
            JwtPayload? body = JsonSerializer.Deserialize<JwtPayload>(
                Encoding.UTF8.GetString(OpaqueTokenCrypto.Base64UrlDecode(segments[1])),
                SerializerOptions);
            if (header is null
                || body is null
                || !string.Equals(header.Alg, "HS256", StringComparison.Ordinal)
                || !string.Equals(header.Typ, "JWT", StringComparison.Ordinal)
                || !string.Equals(body.Iss, authOptions.Issuer, StringComparison.Ordinal)
                || !string.Equals(body.Aud, authOptions.Audience, StringComparison.Ordinal))
            {
                return false;
            }

            DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(body.Iat);
            DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(body.Exp);
            if (issuedAt.Offset != TimeSpan.Zero
                || expiresAt.Offset != TimeSpan.Zero
                || now < issuedAt
                || now >= expiresAt)
            {
                return false;
            }

            payload = new AccessTokenPayload(
                UserId.From(Guid.ParseExact(body.Sub, "N")),
                MembershipId.From(Guid.ParseExact(body.Mid, "N")),
                OrganizationId.From(Guid.ParseExact(body.Oid, "N")),
                DeviceId.From(Guid.ParseExact(body.Did, "N")),
                issuedAt,
                expiresAt);
            return true;
        }
        catch (Exception)
        {
            payload = null;
            return false;
        }
    }

    private sealed record JwtHeader(string Alg, string Typ);

    private sealed record JwtPayload(
        string Iss,
        string Aud,
        string Sub,
        string Mid,
        string Oid,
        string Did,
        long Iat,
        long Exp);
}
