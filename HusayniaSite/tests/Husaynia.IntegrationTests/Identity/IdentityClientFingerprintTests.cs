using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Identity;
using Husaynia.Web.Areas.Admin.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.IntegrationTests.Identity;

[Collection(IdentityAnonymousRateLimitTestGroup.Name)]
public sealed class IdentityClientFingerprintTests
{
    private static readonly byte[] FingerprintKey =
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void FingerprintIsDeterministicFixedLengthHmacAndNormalizesMappedIpv4()
    {
        var admission = CreateAdmission();
        var address = IPAddress.Parse("203.0.113.42");
        var mappedAddress = IPAddress.Parse("::ffff:203.0.113.42");
        var expected = HMACSHA256.HashData(FingerprintKey, address.GetAddressBytes());

        var first = admission.CreateClientFingerprint(address);
        var second = admission.CreateClientFingerprint(address);
        var mapped = admission.CreateClientFingerprint(mappedAddress);

        Assert.Equal(32, first.Length);
        Assert.Equal(expected, first);
        Assert.Equal(first, second);
        Assert.Equal(first, mapped);
        Assert.NotEqual(address.GetAddressBytes(), first);
        Assert.DoesNotContain("203.0.113.42", Convert.ToHexString(first), StringComparison.Ordinal);
    }

    [Fact]
    public void NullAddressUsesAStableHmacRatherThanAClientIdentifier()
    {
        var admission = CreateAdmission();
        var expected = HMACSHA256.HashData(
            FingerprintKey,
            System.Text.Encoding.UTF8.GetBytes("unknown"));

        var fingerprint = admission.CreateClientFingerprint(null);

        Assert.Equal(expected, fingerprint);
        Assert.Equal(32, fingerprint.Length);
    }

    [Fact]
    public async Task SpoofedForwardingHeadersDoNotChangeTheServerAddressFingerprint()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SpoofedForwardingHeadersDoNotChangeTheServerAddressFingerprint));
        var limiter = new CapturingRateLimiter();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            ValidConfiguration(),
            configureServices: services =>
            {
                services.RemoveAll<IIdentityAnonymousRateLimiter>();
                services.AddSingleton<IIdentityAnonymousRateLimiter>(limiter);
            });
        using var client = factory.CreateIdentityClient();
        var antiforgery = await client.GetAntiforgeryTokenAsync();

        foreach (var spoofedAddress in new[] { "198.51.100.10", "192.0.2.99" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/login")
            {
                Content = JsonContent.Create(new { }),
            };
            request.Headers.Add("RequestVerificationToken", antiforgery);
            request.Headers.Add("X-Forwarded-For", spoofedAddress);
            request.Headers.Add("Forwarded", $"for={spoofedAddress}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var fingerprints = limiter.Fingerprints.ToArray();
        Assert.Equal(2, fingerprints.Length);
        Assert.Equal(fingerprints[0], fingerprints[1]);
        Assert.All(fingerprints, fingerprint => Assert.Equal(32, fingerprint.Length));
    }

    private static IdentityAnonymousAdmission CreateAdmission() =>
        new(
            new IdentityModuleOptions
            {
                AnonymousRateLimit = new IdentityAnonymousRateLimitOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(5),
                    Retention = TimeSpan.FromDays(1),
                    FingerprintKey = FingerprintKey,
                },
            },
            new CapturingRateLimiter(),
            TimeProvider.System);

    private static Dictionary<string, string?> ValidConfiguration() =>
        new(StringComparer.Ordinal)
        {
            ["Identity:AnonymousRateLimit:PermitLimit"] = "5",
            ["Identity:AnonymousRateLimit:Window"] = "00:05:00",
            ["Identity:AnonymousRateLimit:Retention"] = "1.00:00:00",
            ["Identity:AnonymousRateLimit:FingerprintKey"] =
                Convert.ToBase64String(FingerprintKey),
        };

    private sealed class CapturingRateLimiter : IIdentityAnonymousRateLimiter
    {
        internal List<byte[]> Fingerprints { get; } = [];

        public Task<IdentityRateLimitDecision> AttemptAsync(
            string endpointFamily,
            ReadOnlyMemory<byte> clientFingerprint,
            CancellationToken cancellationToken)
        {
            Fingerprints.Add(clientFingerprint.ToArray());
            return Task.FromResult(
                new IdentityRateLimitDecision(
                    true,
                    DateTimeOffset.UtcNow.AddMinutes(5),
                    1,
                    5));
        }
    }
}
