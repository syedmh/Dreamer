using System.Text;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Infrastructure.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HusayniaTabruk.Api.ContractTests.Conventions;

public sealed class AuthRateLimitKeyPrivacyTests
{
    [Fact]
    public async Task LoginFingerprintIsHashedAccountScopedAndIndependentOfClientAddress()
    {
        const string email = "Member@Example.Test";
        RateLimitRule accountRule = new(ApiRateLimitPartitions.LoginAccount, 10, TimeSpan.FromMinutes(1));
        RateLimitRule ipRule = new(ApiRateLimitPartitions.IpAddress, 100, TimeSpan.FromMinutes(1));
        ApiRateLimitMetadata metadata = new(
            [accountRule, ipRule],
            ApiRateLimitKeyStrategy.Login);

        IReadOnlyList<(string Key, RateLimitRule Rule)> first = await GetKeysAsync(
            """{"email":"Member@Example.Test","password":"secret"}""",
            "203.0.113.10",
            metadata);
        IReadOnlyList<(string Key, RateLimitRule Rule)> second = await GetKeysAsync(
            """{"email":" member@example.test ","password":"secret"}""",
            "203.0.113.11",
            metadata);
        IReadOnlyList<(string Key, RateLimitRule Rule)> natPeer = await GetKeysAsync(
            """{"email":"manager@example.test","password":"secret"}""",
            "203.0.113.10",
            metadata);

        Assert.Equal(first[0].Key, second[0].Key);
        Assert.NotEqual(first[0].Key, natPeer[0].Key);
        Assert.Equal("203.0.113.10", first[1].Key);
        Assert.DoesNotContain(email, first[0].Key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("member@example.test", first[0].Key, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^login:[0-9A-F]{24}$", first[0].Key);
    }

    [Theory]
    [InlineData(ApiRateLimitKeyStrategy.Invitation, ApiRateLimitPartitions.Invitation, "token", "raw-invitation-token", "invitation")]
    [InlineData(ApiRateLimitKeyStrategy.Refresh, ApiRateLimitPartitions.RefreshToken, "refreshToken", "raw-refresh-token", "refresh")]
    public async Task SecretRateLimitKeysUseFingerprintsAndNeverContainRawBearerValues(
        ApiRateLimitKeyStrategy strategy,
        string partition,
        string property,
        string rawValue,
        string expectedScope)
    {
        RateLimitRule rule = new(partition, 5, TimeSpan.FromMinutes(1));
        ApiRateLimitMetadata metadata = new([rule], strategy);

        IReadOnlyList<(string Key, RateLimitRule Rule)> keys = await GetKeysAsync(
            $$"""{"{{property}}":"{{rawValue}}"}""",
            "198.51.100.9",
            metadata);

        string key = Assert.Single(keys).Key;
        Assert.DoesNotContain(rawValue, key, StringComparison.Ordinal);
        Assert.Matches($"^{expectedScope}:[0-9A-F]{{24}}$", key);
    }

    [Fact]
    public async Task MalformedBodyUsesAddressScopedFingerprintAndLeavesBodyReplayable()
    {
        const string malformedJson = """{"email":""";
        const string address = "192.0.2.44";
        RateLimitRule rule = new(ApiRateLimitPartitions.LoginAccount, 10, TimeSpan.FromMinutes(1));
        ApiRateLimitMetadata metadata = new([rule], ApiRateLimitKeyStrategy.Login);
        DefaultHttpContext context = CreateContext(malformedJson, address);
        EndpointApiRateLimitKeyProvider provider = new(
            new HeaderAddressProvider(),
            new TestRefreshTokenFamilyFingerprintProvider());

        IReadOnlyList<(string Key, RateLimitRule Rule)> keys =
            await provider.GetKeysAsync(context, metadata);

        string key = Assert.Single(keys).Key;
        Assert.Matches("^login:[0-9A-F]{24}$", key);
        Assert.DoesNotContain(address, key, StringComparison.Ordinal);
        Assert.Equal(0, context.Request.Body.Position);
        using StreamReader reader = new(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        Assert.Equal(malformedJson, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task MalformedRefreshTokensShareTheAddressFallbackInsteadOfCreatingUnboundedBuckets()
    {
        RateLimitRule rule = new(ApiRateLimitPartitions.RefreshToken, 30, TimeSpan.FromMinutes(1));
        ApiRateLimitMetadata metadata = new([rule], ApiRateLimitKeyStrategy.Refresh);

        IReadOnlyList<(string Key, RateLimitRule Rule)> first = await GetKeysAsync(
            """{"refreshToken":"malformed-one"}""",
            "192.0.2.50",
            metadata);
        IReadOnlyList<(string Key, RateLimitRule Rule)> second = await GetKeysAsync(
            """{"refreshToken":"malformed-two"}""",
            "192.0.2.50",
            metadata);
        IReadOnlyList<(string Key, RateLimitRule Rule)> otherAddress = await GetKeysAsync(
            """{"refreshToken":"malformed-three"}""",
            "192.0.2.51",
            metadata);

        Assert.Equal(first[0].Key, second[0].Key);
        Assert.NotEqual(first[0].Key, otherAddress[0].Key);
        Assert.Matches("^refresh:[0-9A-F]{24}$", first[0].Key);
    }

    [Fact]
    public void RefreshFingerprintUsesOnlyTheStableFamilyLocator()
    {
        RefreshTokenFamilyFingerprintProvider provider = new(
            Options.Create(
                new TabrukAuthOptions
                {
                    SigningKey = "contract-test-signing-key-0123456789abcdef",
                }));
        byte[] firstFamily = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        byte[] secondFamily = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
        string firstRotation = CreateOpaqueToken(firstFamily, 32);
        string secondRotation = CreateOpaqueToken(firstFamily, 64);
        string independentFamily = CreateOpaqueToken(secondFamily, 32);

        Assert.True(provider.TryGetFingerprint(firstRotation, out string firstFingerprint));
        Assert.True(provider.TryGetFingerprint(secondRotation, out string secondFingerprint));
        Assert.True(provider.TryGetFingerprint(independentFamily, out string independentFingerprint));
        Assert.Equal(firstFingerprint, secondFingerprint);
        Assert.NotEqual(firstFingerprint, independentFingerprint);
        Assert.Matches("^[0-9A-F]{24}$", firstFingerprint);
        Assert.False(provider.TryGetFingerprint("malformed", out string malformedFingerprint));
        Assert.Empty(malformedFingerprint);
    }

    private static async Task<IReadOnlyList<(string Key, RateLimitRule Rule)>> GetKeysAsync(
        string json,
        string address,
        ApiRateLimitMetadata metadata)
    {
        DefaultHttpContext context = CreateContext(json, address);
        EndpointApiRateLimitKeyProvider provider = new(
            new HeaderAddressProvider(),
            new TestRefreshTokenFamilyFingerprintProvider());
        return await provider.GetKeysAsync(context, metadata);
    }

    private static DefaultHttpContext CreateContext(string json, string address)
    {
        DefaultHttpContext context = new();
        context.Request.Headers["X-Test-Client-Address"] = address;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Request.ContentLength = context.Request.Body.Length;
        return context;
    }

    private static string CreateOpaqueToken(byte[] locator, byte secretByte)
    {
        byte[] token = new byte[48];
        locator.CopyTo(token, 0);
        token.AsSpan(16).Fill(secretByte);
        return Convert.ToBase64String(token)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class HeaderAddressProvider : IApiClientAddressProvider
    {
        public string GetAddress(HttpContext context) =>
            context.Request.Headers["X-Test-Client-Address"].ToString();
    }

    private sealed class TestRefreshTokenFamilyFingerprintProvider : IRefreshTokenFamilyFingerprintProvider
    {
        public bool TryGetFingerprint(string refreshToken, out string fingerprint)
        {
            fingerprint = refreshToken == "raw-refresh-token"
                ? "0123456789ABCDEF01234567"
                : string.Empty;
            return fingerprint.Length > 0;
        }
    }
}
