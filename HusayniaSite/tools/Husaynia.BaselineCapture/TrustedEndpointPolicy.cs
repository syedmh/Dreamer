using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Husaynia.BaselineCapture;

public interface ITrustedDnsResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed record EndpointRequest(
    Uri Uri,
    string Method,
    string ResourceType,
    bool IsNavigation,
    bool IsMainFrame);

public sealed record EndpointDecision(
    bool Allowed,
    string ReasonCode,
    IPAddress? PinnedAddress);

public enum TrustedHostClass
{
    PrimaryOrigin,
    StaticResource
}

public sealed record DnsPinBinding(
    string Host,
    TrustedHostClass HostClass,
    IPAddress SelectedAddress,
    IReadOnlyList<IPAddress> CompleteObservedPublicSet,
    DateTimeOffset ObservedAtUtc,
    string AnswerSetSha256,
    bool Rotated);

public sealed record TrustedContextNetworkPolicy(
    long Epoch,
    IReadOnlyDictionary<string, DnsPinBinding> Bindings,
    IReadOnlyList<string> ChromiumHostResolverRules);

public sealed class TrustedEndpointPolicy
{
    private readonly CaptureProfile _profile;
    private readonly ITrustedDnsResolver _resolver;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> _approvedAddressSets;
    private readonly IReadOnlyDictionary<string, IPAddress> _pinnedAddresses;
    private readonly Dictionary<string, StaticResourceRule> _staticRules;
    private readonly object _contextSync = new();
    private IReadOnlyDictionary<string, DnsPinBinding>? _previousContextBindings;
    private long _nextEpoch;

    private TrustedEndpointPolicy(
        CaptureProfile profile,
        ITrustedDnsResolver resolver,
        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> approvedAddressSets,
        IReadOnlyDictionary<string, IPAddress> pinnedAddresses)
    {
        _profile = profile;
        _resolver = resolver;
        _approvedAddressSets = approvedAddressSets;
        _pinnedAddresses = pinnedAddresses;
        _staticRules = profile.StaticResources.ToDictionary(rule => rule.Host, StringComparer.OrdinalIgnoreCase);
        ChromiumHostResolverRules = Array.AsReadOnly(pinnedAddresses
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"MAP {pair.Key} {FormatChromiumAddress(pair.Value)}")
            .Append("MAP * ~NOTFOUND")
            .ToArray());
        ApprovedDnsAnswers = new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            approvedAddressSets.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)Array.AsReadOnly(
                    pair.Value.Select(address => address.ToString()).ToArray()),
                StringComparer.OrdinalIgnoreCase));
        _previousContextBindings = new ReadOnlyDictionary<string, DnsPinBinding>(
            approvedAddressSets.ToDictionary(
                pair => pair.Key,
                pair => new DnsPinBinding(
                    pair.Key,
                    pair.Key.Equals(
                        profile.PrimaryOrigin.Host,
                        StringComparison.OrdinalIgnoreCase)
                        ? TrustedHostClass.PrimaryOrigin
                        : TrustedHostClass.StaticResource,
                    pinnedAddresses[pair.Key],
                    pair.Value,
                    DateTimeOffset.MinValue,
                    ComputeAnswerSetSha256(pair.Value),
                    false),
                StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> ChromiumHostResolverRules { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ApprovedDnsAnswers { get; }

    public async Task<TrustedContextNetworkPolicy> CreateContextPolicyAsync(
        CancellationToken cancellationToken)
    {
        var bindings = new Dictionary<string, DnsPinBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in _approvedAddressSets.Keys.Order(StringComparer.Ordinal))
        {
            var observedAtUtc = DateTimeOffset.UtcNow;
            var addresses = await ResolvePublicSetAsync(_resolver, host, cancellationToken);
            var hostClass = host.Equals(
                _profile.PrimaryOrigin.Host,
                StringComparison.OrdinalIgnoreCase)
                ? TrustedHostClass.PrimaryOrigin
                : TrustedHostClass.StaticResource;
            if (hostClass == TrustedHostClass.PrimaryOrigin
                && !_approvedAddressSets[host].SequenceEqual(addresses))
            {
                throw new CaptureSafetyException("origin-dns-set-changed");
            }

            bool rotated;
            lock (_contextSync)
            {
                rotated = _previousContextBindings is not null
                    && _previousContextBindings.TryGetValue(host, out var previous)
                    && !previous.SelectedAddress.Equals(addresses[0]);
            }

            bindings.Add(
                host,
                new DnsPinBinding(
                    host,
                    hostClass,
                    addresses[0],
                    Array.AsReadOnly(addresses.ToArray()),
                    observedAtUtc,
                    ComputeAnswerSetSha256(addresses),
                    rotated));
        }

        long epoch;
        IReadOnlyDictionary<string, DnsPinBinding> immutableBindings;
        lock (_contextSync)
        {
            epoch = ++_nextEpoch;
            immutableBindings = new ReadOnlyDictionary<string, DnsPinBinding>(
                new Dictionary<string, DnsPinBinding>(
                    bindings,
                    StringComparer.OrdinalIgnoreCase));
            _previousContextBindings = immutableBindings;
        }

        return new TrustedContextNetworkPolicy(
            epoch,
            immutableBindings,
            CreateChromiumHostResolverRules(
                immutableBindings.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.SelectedAddress,
                    StringComparer.OrdinalIgnoreCase)));
    }

    public static async Task<TrustedEndpointPolicy> CreateAsync(
        CaptureProfile profile,
        ITrustedDnsResolver resolver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(resolver);

        var hosts = profile.StaticResources.Select(rule => rule.Host)
            .Append(profile.PrimaryOrigin.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sets = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var pinned = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            var addresses = await ResolvePublicSetAsync(resolver, host, cancellationToken);
            sets.Add(host, addresses);
            pinned.Add(host, addresses[0]);
        }

        return new TrustedEndpointPolicy(profile, resolver, sets, pinned);
    }

    public async ValueTask<EndpointDecision> AuthorizeAsync(
        EndpointRequest request,
        CancellationToken cancellationToken)
        => await AuthorizeCoreAsync(
            request,
            _pinnedAddresses,
            validateCurrentDns: false,
            cancellationToken);

    public async ValueTask<EndpointDecision> AuthorizeAsync(
        TrustedContextNetworkPolicy contextPolicy,
        EndpointRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextPolicy);
        var pinned = contextPolicy.Bindings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.SelectedAddress,
            StringComparer.OrdinalIgnoreCase);
        return await AuthorizeCoreAsync(
            request,
            pinned,
            validateCurrentDns: true,
            cancellationToken);
    }

    private async ValueTask<EndpointDecision> AuthorizeCoreAsync(
        EndpointRequest request,
        IReadOnlyDictionary<string, IPAddress> pinnedAddresses,
        bool validateCurrentDns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var uriDecision = ValidateUri(request.Uri);
        if (!uriDecision.Allowed)
        {
            return uriDecision;
        }

        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "method-not-allowed", null);
        }

        if (EvidenceSanitizer.HasSensitiveQueryParameter(request.Uri))
        {
            return new(false, "sensitive-query-parameter", null);
        }

        var host = request.Uri.Host;
        if (!pinnedAddresses.TryGetValue(host, out var pinnedAddress))
        {
            return new(false, "host-not-approved", null);
        }

        var sameOrigin = host.Equals(_profile.PrimaryOrigin.Host, StringComparison.OrdinalIgnoreCase);
        if (validateCurrentDns)
        {
            var current = await ResolvePublicSetAsync(_resolver, host, cancellationToken);
            if (sameOrigin && !_approvedAddressSets[host].SequenceEqual(current))
            {
                return new(false, "origin-dns-set-changed", pinnedAddress);
            }
        }

        if (sameOrigin)
        {
            if (EndpointSafetyClassifier.IsSensitiveEndpoint(request.Uri))
            {
                return new(false, "sensitive-endpoint-blocked", pinnedAddress);
            }

            return new(true, "primary-origin-get-head", pinnedAddress);
        }

        if (request.IsNavigation || request.ResourceType.Equals("document", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "outside-origin-navigation", pinnedAddress);
        }

        if (!_staticRules.TryGetValue(host, out var rule))
        {
            return new(false, "host-not-approved", null);
        }

        if (!rule.ResourceTypes.Contains(request.ResourceType))
        {
            return new(false, "static-resource-type-not-approved", pinnedAddress);
        }

        return new(true, "approved-static-resource", pinnedAddress);
    }

    public async Task AssertDnsSetsUnchangedAsync(CancellationToken cancellationToken)
    {
        foreach (var expected in _approvedAddressSets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await AssertHostDnsSetUnchangedAsync(expected.Key, cancellationToken);
        }
    }

    public async Task AssertHostDnsSetUnchangedAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (!_approvedAddressSets.TryGetValue(host, out var expected))
        {
            throw new CaptureSafetyException("host-not-approved");
        }

        var actual = await ResolvePublicSetAsync(_resolver, host, cancellationToken);
        if (host.Equals(_profile.PrimaryOrigin.Host, StringComparison.OrdinalIgnoreCase)
            && !expected.SequenceEqual(actual))
        {
            throw new CaptureSafetyException("origin-dns-set-changed");
        }
    }

    public async Task AssertContextDnsSafeAsync(
        TrustedContextNetworkPolicy contextPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contextPolicy);
        foreach (var binding in contextPolicy.Bindings.Values.OrderBy(
                     binding => binding.Host,
                     StringComparer.Ordinal))
        {
            var actual = await ResolvePublicSetAsync(
                _resolver,
                binding.Host,
                cancellationToken);
            if (binding.HostClass == TrustedHostClass.PrimaryOrigin
                && !_approvedAddressSets[binding.Host].SequenceEqual(actual))
            {
                throw new CaptureSafetyException("origin-dns-set-changed");
            }
        }
    }

    public SocketsHttpHandler CreateCrawlHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            Credentials = null,
            PreAuthenticate = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (context.DnsEndPoint.Port != 443
                    || !_pinnedAddresses.TryGetValue(context.DnsEndPoint.Host, out var address))
                {
                    throw new HttpRequestException("connect-endpoint-not-approved");
                }

                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private EndpointDecision ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return new(false, "absolute-uri-required", null);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new(false, "credentials-not-allowed", null);
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            return new(false, "ip-literal-not-allowed", null);
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "https-required", null);
        }

        if (uri.Port != 443)
        {
            return new(false, "port-not-allowed", null);
        }

        if (!_pinnedAddresses.ContainsKey(uri.Host))
        {
            return new(false, "host-not-approved", null);
        }

        return new(true, "uri-approved", _pinnedAddresses[uri.Host]);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicSetAsync(
        ITrustedDnsResolver resolver,
        string host,
        CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(host, cancellationToken);
        var addresses = resolved
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (addresses.Length == 0)
        {
            throw new CaptureSafetyException("dns-no-answer");
        }

        if (addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new CaptureSafetyException("dns-non-public-answer");
        }

        return addresses;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !Ipv4SpecialUsePrefixes.Any(prefix => PrefixMatches(bytes, prefix.Network, prefix.Bits));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast)
        {
            return false;
        }

        if (!PrefixMatches(
                bytes,
                IPAddress.Parse("2000::").GetAddressBytes(),
                3))
        {
            return false;
        }

        return !Ipv6SpecialUsePrefixes.Any(prefix => PrefixMatches(bytes, prefix.Network, prefix.Bits));
    }

    private static readonly (byte[] Network, int Bits)[] Ipv4SpecialUsePrefixes =
    [
        (IPAddress.Parse("0.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("10.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("100.64.0.0").GetAddressBytes(), 10),
        (IPAddress.Parse("127.0.0.0").GetAddressBytes(), 8),
        (IPAddress.Parse("169.254.0.0").GetAddressBytes(), 16),
        (IPAddress.Parse("172.16.0.0").GetAddressBytes(), 12),
        (IPAddress.Parse("192.0.0.0").GetAddressBytes(), 24),
        (IPAddress.Parse("192.0.2.0").GetAddressBytes(), 24),
        (IPAddress.Parse("192.31.196.0").GetAddressBytes(), 24),
        (IPAddress.Parse("192.52.193.0").GetAddressBytes(), 24),
        (IPAddress.Parse("192.88.99.0").GetAddressBytes(), 24),
        (IPAddress.Parse("192.168.0.0").GetAddressBytes(), 16),
        (IPAddress.Parse("192.175.48.0").GetAddressBytes(), 24),
        (IPAddress.Parse("198.18.0.0").GetAddressBytes(), 15),
        (IPAddress.Parse("198.51.100.0").GetAddressBytes(), 24),
        (IPAddress.Parse("203.0.113.0").GetAddressBytes(), 24),
        (IPAddress.Parse("224.0.0.0").GetAddressBytes(), 4),
        (IPAddress.Parse("240.0.0.0").GetAddressBytes(), 4)
    ];

    private static readonly (byte[] Network, int Bits)[] Ipv6SpecialUsePrefixes =
    [
        (IPAddress.Parse("::").GetAddressBytes(), 96),
        (IPAddress.Parse("64:ff9b::").GetAddressBytes(), 96),
        (IPAddress.Parse("64:ff9b:1::").GetAddressBytes(), 48),
        (IPAddress.Parse("100::").GetAddressBytes(), 64),
        (IPAddress.Parse("2001::").GetAddressBytes(), 23),
        (IPAddress.Parse("2001:db8::").GetAddressBytes(), 32),
        (IPAddress.Parse("2002::").GetAddressBytes(), 16),
        (IPAddress.Parse("2620:4f:8000::").GetAddressBytes(), 48),
        (IPAddress.Parse("3fff::").GetAddressBytes(), 20),
        (IPAddress.Parse("5f00::").GetAddressBytes(), 16),
        (IPAddress.Parse("fc00::").GetAddressBytes(), 7),
        (IPAddress.Parse("fe80::").GetAddressBytes(), 10),
        (IPAddress.Parse("fec0::").GetAddressBytes(), 10),
        (IPAddress.Parse("ff00::").GetAddressBytes(), 8)
    ];

    private static bool PrefixMatches(byte[] address, byte[] network, int bits)
    {
        var fullBytes = bits / 8;
        var remainingBits = bits % 8;
        if (!address.AsSpan(0, fullBytes).SequenceEqual(network.AsSpan(0, fullBytes)))
        {
            return false;
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remainingBits));
        return (address[fullBytes] & mask) == (network[fullBytes] & mask);
    }

    private static string FormatChromiumAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    internal static ReadOnlyCollection<string> CreateChromiumHostResolverRules(
        IReadOnlyDictionary<string, IPAddress> pinnedAddresses) =>
        Array.AsReadOnly(pinnedAddresses
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"MAP {pair.Key} {FormatChromiumAddress(pair.Value)}")
            .Append("MAP * ~NOTFOUND")
            .ToArray());

    private static string ComputeAnswerSetSha256(IReadOnlyList<IPAddress> addresses) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        string.Join('\n', addresses.Select(address => address.ToString())))))
            .ToLowerInvariant();

    internal static string ComputeResolverMapSha256(
        IReadOnlyList<string> chromiumHostResolverRules) =>
        CaptureIO.Sha256(
            Encoding.UTF8.GetBytes(
                string.Join('\n', chromiumHostResolverRules)));
}

public sealed class SystemTrustedDnsResolver : ITrustedDnsResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class CaptureSafetyException(string reasonCode) : InvalidOperationException(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}
