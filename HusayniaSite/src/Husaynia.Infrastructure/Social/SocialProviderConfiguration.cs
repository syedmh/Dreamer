using Husaynia.Application.Contracts;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Microsoft.Extensions.Configuration;

namespace Husaynia.Infrastructure.Social;

internal static class SocialProviderConfiguration
{
    internal const string ProvidersSection = "Social:Providers";

    internal static (
        IReadOnlyDictionary<string, SocialProviderSettings> Providers,
        IReadOnlyCollection<string> Errors) Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var providers = new Dictionary<string, SocialProviderSettings>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var section in configuration.GetSection(ProvidersSection).GetChildren())
        {
            if (!Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint))
            {
                errors.Add($"{section.Path}:Endpoint must be an absolute HTTPS URI.");
                continue;
            }

            var sourceHosts = ReadHosts(section, "SourceHosts");
            var mediaHosts = ReadHosts(section, "MediaHosts");
            try
            {
                var settings = new SocialProviderSettings(
                    section.Key,
                    endpoint,
                    sourceHosts,
                    mediaHosts,
                    ParseInt(section["MaximumResponseBytes"], 1_000_000),
                    TimeSpan.FromSeconds(ParseInt(section["ConnectTimeoutSeconds"], 5)),
                    TimeSpan.FromSeconds(ParseInt(section["RequestTimeoutSeconds"], 10)),
                    ParseInt(section["MaximumAttempts"], 2),
                    TimeSpan.FromMilliseconds(ParseInt(section["MaximumRetryDelayMilliseconds"], 2_000)),
                    ParseInt(section["CircuitFailureThreshold"], 3),
                    TimeSpan.FromSeconds(ParseInt(section["CircuitBreakSeconds"], 60)));
                providers.Add(settings.Provider, settings);
            }
            catch (ArgumentException)
            {
                errors.Add($"{section.Path} contains invalid or unsafe provider settings.");
            }
        }

        return (providers, errors);
    }

    private static HashSet<string> ReadHosts(
        IConfigurationSection provider,
        string name) =>
        provider.GetSection(name)
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int ParseInt(string? value, int defaultValue) =>
        int.TryParse(value, out var parsed) ? parsed : defaultValue;
}

public sealed class ConfigurationSocialCredentialSource(IConfiguration configuration)
    : ISocialProviderCredentialSource
{
    public ValueTask<string?> GetBearerTokenAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        return ValueTask.FromResult(
            configuration[
                $"{SocialProviderConfiguration.ProvidersSection}:{normalizedProvider}:BearerToken"]);
    }
}

public sealed class ConfiguredSocialFeedProvider(
    ISocialProviderHttpClientFactory httpClientFactory,
    IReadOnlyDictionary<string, SocialProviderSettings> settings,
    ISocialProviderCredentialSource credentialSource)
    : ISocialFeedSource, ISocialFeedProvider
{
    public Task<Result<SocialProviderFeed, SocialSourceError>> FetchAsync(
        SocialFeedRequest request,
        CancellationToken cancellationToken)
    {
        var provider = SocialFeedValidation.NormalizeProvider(request.Provider);
        return settings.TryGetValue(provider, out var providerSettings)
            ? new JsonSocialFeedProvider(
                    httpClientFactory,
                    providerSettings,
                    credentialSource)
                .FetchAsync(request, cancellationToken)
            : Task.FromResult(Result.Fail<SocialProviderFeed, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.Unavailable)));
    }

    async Task<Result<SocialFeedSnapshot, IntegrationError>> ISocialFeedProvider.FetchAsync(
        SocialFeedRequest request,
        CancellationToken ct)
    {
        var result = await FetchAsync(request, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result.Fail<SocialFeedSnapshot, IntegrationError>(
                new IntegrationError(
                    result.Error.Kind.ToString().ToLowerInvariant(),
                    "The social provider is unavailable."));
        }

        return Result.Succeed<SocialFeedSnapshot, IntegrationError>(
            new SocialFeedSnapshot(
                result.Success.Provider,
                result.Success.Items.Select(item => new SocialFeedItem(
                    item.ExternalId,
                    item.Text,
                    item.SourceLink,
                    item.PublishedAtUtc)).ToArray(),
                result.Success.FetchedAtUtc));
    }
}

public sealed class SocialConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration) =>
        SocialProviderConfiguration.Read(configuration).Errors;
}
