using System.Net;
using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class TrustedEndpointPolicyTests
{
    [Fact]
    public async Task ApprovedOriginAndStaticResourcesUsePinnedPublicAddresses()
    {
        var resolver = StableResolver(IPAddress.Parse("93.184.216.34"));
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);

        var document = await policy.AuthorizeAsync(
            new EndpointRequest(
                new Uri("https://www.husaynia.org/contact-us/"),
                "GET",
                "document",
                true,
                true),
            CancellationToken.None);
        var font = await policy.AuthorizeAsync(
            new EndpointRequest(
                new Uri("https://fonts.gstatic.com/font.woff2"),
                "GET",
                "font",
                false,
                false),
            CancellationToken.None);

        Assert.True(document.Allowed);
        Assert.Equal("primary-origin-get-head", document.ReasonCode);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), document.PinnedAddress);
        Assert.True(font.Allowed);
        Assert.Equal("approved-static-resource", font.ReasonCode);
        Assert.Equal(6, policy.ChromiumHostResolverRules.Count);
        Assert.Equal("MAP * ~NOTFOUND", policy.ChromiumHostResolverRules[^1]);
        using var handler = policy.CreateCrawlHandler();
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("https://user:password@www.husaynia.org/", "credentials-not-allowed")]
    [InlineData("https://127.0.0.1/", "ip-literal-not-allowed")]
    [InlineData("http://www.husaynia.org/", "https-required")]
    [InlineData("https://www.husaynia.org:444/", "port-not-allowed")]
    [InlineData("https://untrusted.example/", "host-not-approved")]
    public async Task InvalidTrustBoundaryInputsFailClosed(string rawUri, string reason)
    {
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            StableResolver(IPAddress.Parse("93.184.216.34")),
            CancellationToken.None);

        var decision = await policy.AuthorizeAsync(
            new EndpointRequest(new Uri(rawUri), "GET", "document", true, true),
            CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(reason, decision.ReasonCode);
    }

    [Fact]
    public async Task PrivateOrReservedDnsAnswersAreRejected()
    {
        var resolver = StableResolver(IPAddress.Loopback);

        var exception = await Assert.ThrowsAsync<CaptureSafetyException>(() =>
            TrustedEndpointPolicy.CreateAsync(
                CaptureProfile.Approved,
                resolver,
                CancellationToken.None));

        Assert.Equal("dns-non-public-answer", exception.ReasonCode);
    }

    [Fact]
    public async Task DnsAnswerSetChangesAreRejectedBeforeAttempt()
    {
        var resolver = new SequencedResolver(
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("93.184.216.35"));
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CaptureSafetyException>(() =>
            policy.AssertDnsSetsUnchangedAsync(CancellationToken.None));

        Assert.Equal("origin-dns-set-changed", exception.ReasonCode);
    }

    [Fact]
    public async Task PublicStaticRotationCreatesNewImmutableContextEpochAndSortedPin()
    {
        var resolver = new DelegatingResolver((host, call) =>
            host == CaptureProfile.Approved.PrimaryOrigin.Host
                ? [IPAddress.Parse("93.184.216.34")]
                : call switch
                {
                    0 => [IPAddress.Parse("93.184.216.35")],
                    1 => [IPAddress.Parse("93.184.216.37"), IPAddress.Parse("93.184.216.36")],
                    _ => [IPAddress.Parse("93.184.216.39"), IPAddress.Parse("93.184.216.38")]
                });
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);

        var first = await policy.CreateContextPolicyAsync(CancellationToken.None);
        var second = await policy.CreateContextPolicyAsync(CancellationToken.None);
        var rule = CaptureProfile.Approved.StaticResources[0];
        var host = rule.Host;

        Assert.Equal(1, first.Epoch);
        Assert.Equal(2, second.Epoch);
        Assert.Equal(
            IPAddress.Parse("93.184.216.38"),
            second.Bindings[host].SelectedAddress);
        Assert.True(second.Bindings[host].Rotated);
        Assert.Equal(64, second.Bindings[host].AnswerSetSha256.Length);
        Assert.Equal("MAP * ~NOTFOUND", second.ChromiumHostResolverRules[^1]);
        Assert.Throws<NotSupportedException>((Action)(() =>
            ((IDictionary<string, DnsPinBinding>)second.Bindings).Add(
                "example.com",
                second.Bindings[host])));
        Assert.Throws<NotSupportedException>((Action)(() =>
            ((IList<IPAddress>)second.Bindings[host].CompleteObservedPublicSet).Add(
                IPAddress.Parse("8.8.8.8"))));
    }

    [Fact]
    public async Task FirstContextComparesSelectedPinsAgainstBootstrapBindings()
    {
        var rotatedHost = CaptureProfile.Approved.StaticResources[0].Host;
        var resolver = new DelegatingResolver((host, call) =>
            host == CaptureProfile.Approved.PrimaryOrigin.Host
                || host != rotatedHost
                || call == 0
                ? [IPAddress.Parse("93.184.216.34")]
                : [IPAddress.Parse("93.184.216.35")]);
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);

        var first = await policy.CreateContextPolicyAsync(CancellationToken.None);

        Assert.True(first.Bindings[rotatedHost].Rotated);
        Assert.Equal(
            IPAddress.Parse("93.184.216.35"),
            first.Bindings[rotatedHost].SelectedAddress);
        Assert.All(
            first.Bindings.Values.Where(binding => binding.Host != rotatedHost),
            binding => Assert.False(binding.Rotated));
    }

    [Fact]
    public async Task ChangedResolverMapRelaunchesBrowserWhileIdenticalMapReusesIt()
    {
        var initialRules = new[] { "MAP example.com 93.184.216.34", "MAP * ~NOTFOUND" };
        var changedRules = new[] { "MAP example.com 93.184.216.35", "MAP * ~NOTFOUND" };
        var initial = new FakeBrowserSession("initial");
        var created = new List<FakeBrowserSession>();
        var samePolicy = new TrustedContextNetworkPolicy(
            1,
            new Dictionary<string, DnsPinBinding>(),
            initialRules);
        var changedPolicy = samePolicy with
        {
            Epoch = 2,
            ChromiumHostResolverRules = changedRules
        };

        var reused = await PlaywrightScreenshotCapture
            .EnsureBrowserForContextPolicyAsync(
                initial,
                initialRules,
                samePolicy,
                static session => session.DisposeAsync(),
                (rules, _) =>
                {
                    var session = new FakeBrowserSession(string.Join(',', rules));
                    created.Add(session);
                    return Task.FromResult(session);
                },
                CancellationToken.None);

        Assert.Same(initial, reused.Session);
        Assert.Equal(0, initial.DisposeCount);
        Assert.Empty(created);

        var relaunched = await PlaywrightScreenshotCapture
            .EnsureBrowserForContextPolicyAsync(
                reused.Session,
                reused.ResolverRules,
                changedPolicy,
                static session => session.DisposeAsync(),
                (rules, _) =>
                {
                    var session = new FakeBrowserSession(string.Join(',', rules));
                    created.Add(session);
                    return Task.FromResult(session);
                },
                CancellationToken.None);

        Assert.Equal(1, initial.DisposeCount);
        var replacement = Assert.Single(created);
        Assert.Same(replacement, relaunched.Session);
        Assert.True(changedRules.SequenceEqual(relaunched.ResolverRules));

        await relaunched.Session.DisposeAsync();
        Assert.Equal(1, replacement.DisposeCount);
    }

    [Fact]
    public async Task AuthorizationRevalidatesPublicAnswersWithoutChangingContextPin()
    {
        var resolver = new DelegatingResolver((host, call) =>
            host == CaptureProfile.Approved.PrimaryOrigin.Host
                ? [IPAddress.Parse("93.184.216.34")]
                : [IPAddress.Parse(call < 2 ? "93.184.216.35" : "93.184.216.36")]);
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);
        var contextPolicy = await policy.CreateContextPolicyAsync(CancellationToken.None);
        var rule = CaptureProfile.Approved.StaticResources[0];
        var host = rule.Host;
        var activePin = contextPolicy.Bindings[host].SelectedAddress;

        var decision = await policy.AuthorizeAsync(
            contextPolicy,
            new EndpointRequest(
                new Uri($"https://{host}/font.woff2"),
                "GET",
                rule.ResourceTypes.First(),
                false,
                false),
            CancellationToken.None);

        Assert.True(decision.Allowed);
        Assert.Equal(activePin, decision.PinnedAddress);
        Assert.Equal(activePin, contextPolicy.Bindings[host].SelectedAddress);
    }

    [Fact]
    public async Task MixedPublicPrivateStaticRotationFailsClosed()
    {
        var resolver = new DelegatingResolver((host, call) =>
            host == CaptureProfile.Approved.PrimaryOrigin.Host || call == 0
                ? [IPAddress.Parse("93.184.216.34")]
                : [IPAddress.Parse("93.184.216.35"), IPAddress.Loopback]);
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CaptureSafetyException>(() =>
            policy.CreateContextPolicyAsync(CancellationToken.None));

        Assert.Equal("dns-non-public-answer", exception.ReasonCode);
    }

    [Fact]
    public async Task OriginChangeFailsContextCreationWithStableReason()
    {
        var resolver = new DelegatingResolver((host, call) =>
            host == CaptureProfile.Approved.PrimaryOrigin.Host && call > 0
                ? [IPAddress.Parse("93.184.216.35")]
                : [IPAddress.Parse("93.184.216.34")]);
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            resolver,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CaptureSafetyException>(() =>
            policy.CreateContextPolicyAsync(CancellationToken.None));

        Assert.Equal("origin-dns-set-changed", exception.ReasonCode);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("203.0.113.1")]
    [InlineData("192.31.196.1")]
    [InlineData("192.52.193.1")]
    [InlineData("192.88.99.1")]
    [InlineData("192.175.48.1")]
    [InlineData("::1")]
    [InlineData("::10.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:10::1")]
    [InlineData("2001:20::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("2620:4f:8000::1")]
    [InlineData("3fff::1")]
    [InlineData("5f00::1")]
    [InlineData("4000::1")]
    public void ReservedAddressClassifierRejectsNonPublicRanges(string value)
    {
        Assert.False(TrustedEndpointPolicy.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    public void PublicAddressClassifierAllowsGlobalUnicast(string value)
    {
        Assert.True(TrustedEndpointPolicy.IsPublicAddress(IPAddress.Parse(value)));
    }

    [Fact]
    public async Task SignedCredentialQueryPatternsAreRejectedWithoutLoggingValues()
    {
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            StableResolver(IPAddress.Parse("93.184.216.34")),
            CancellationToken.None);
        var uri = new Uri("https://www.husaynia.org/image.png?X-Amz-Signature=super-secret&ver=1");

        var decision = await policy.AuthorizeAsync(
            new EndpointRequest(uri, "GET", "image", false, false),
            CancellationToken.None);
        var redacted = EvidenceSanitizer.RedactUrl(uri.AbsoluteUri);

        Assert.False(decision.Allowed);
        Assert.Equal("sensitive-query-parameter", decision.ReasonCode);
        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ver=1", redacted, StringComparison.Ordinal);
        Assert.Contains("REDACTED-SENSITIVE", Uri.UnescapeDataString(redacted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedQueryNamesFailClosed()
    {
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            StableResolver(IPAddress.Parse("93.184.216.34")),
            CancellationToken.None);

        var decision = await policy.AuthorizeAsync(
            new EndpointRequest(
                new Uri("https://www.husaynia.org/resource?%zz=value"),
                "GET",
                "document",
                true,
                true),
            CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal("sensitive-query-parameter", decision.ReasonCode);
    }

    [Theory]
    [InlineData("/wp-json/contact-form-7/v1/contact-forms/1/feedback")]
    [InlineData("/wpforms/submit")]
    [InlineData("/register/")]
    [InlineData("/signup/")]
    [InlineData("/oauth/authorize")]
    [InlineData("/password-reset/")]
    [InlineData("/wp-login.php")]
    [InlineData("/my-account/")]
    [InlineData("/checkout/")]
    [InlineData("/payments/confirm")]
    public async Task ProductionPolicyBlocksSameOriginFormAuthAndMutationEndpoints(string path)
    {
        var policy = await TrustedEndpointPolicy.CreateAsync(
            CaptureProfile.Approved,
            StableResolver(IPAddress.Parse("93.184.216.34")),
            CancellationToken.None);

        foreach (var method in new[] { "GET", "HEAD" })
        {
            var decision = await policy.AuthorizeAsync(
                new EndpointRequest(
                    new Uri(CaptureProfile.Approved.PrimaryOrigin, path),
                    method,
                    "document",
                    true,
                    true),
                CancellationToken.None);

            Assert.False(decision.Allowed);
            Assert.Equal("sensitive-endpoint-blocked", decision.ReasonCode);
        }
    }

    [Fact]
    public void ApprovedCaptureProfileIsDeeplyImmutable()
    {
        var profile = CaptureProfile.Approved;

        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, Uri>)profile.Representatives).Add(
                "mutated",
                new Uri("https://example.com/")));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CaptureViewport>)profile.Viewports).Add(new("mutated", 1, 1)));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<StaticResourceRule>)profile.StaticResources).Add(
                new("example.com", new HashSet<string>())));
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<BrowserIdentityProfile>)profile.BrowserIdentities).Add(
                profile.GetCurrentBrowserIdentity()));
        if (profile.StaticResources[0].ResourceTypes is ICollection<string> mutableResourceTypes)
        {
            Assert.Throws<NotSupportedException>(() => mutableResourceTypes.Add("script"));
        }
        else
        {
            Assert.False(profile.StaticResources[0].ResourceTypes is ISet<string>);
        }
        Assert.DoesNotContain("mutated", profile.Representatives.Keys);
        Assert.DoesNotContain(profile.Viewports, viewport => viewport.Name == "mutated");
    }

    private static StableDnsResolver StableResolver(IPAddress address) =>
        new StableDnsResolver(address);

    private sealed class StableDnsResolver(IPAddress address) : ITrustedDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([address]);
    }

    private sealed class FakeBrowserSession(string id) : IAsyncDisposable
    {
        public string Id { get; } = id;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequencedResolver(
        IPAddress initialAddress,
        IPAddress changedAddress) : ITrustedDnsResolver
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            _calls.TryGetValue(host, out var calls);
            _calls[host] = calls + 1;
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                [calls == 0 ? initialAddress : changedAddress]);
        }

    }

    private sealed class DelegatingResolver(
        Func<string, int, IReadOnlyList<IPAddress>> resolve) : ITrustedDnsResolver
    {
        private readonly Dictionary<string, int> _calls =
            new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            _calls.TryGetValue(host, out var calls);
            _calls[host] = calls + 1;
            return ValueTask.FromResult(resolve(host, calls));
        }
    }
}
