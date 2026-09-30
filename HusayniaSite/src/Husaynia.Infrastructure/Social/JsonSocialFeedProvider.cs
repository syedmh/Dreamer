using System.Text.Json;
using System.Text.Json.Serialization;
using Husaynia.Application.Contracts;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;

namespace Husaynia.Infrastructure.Social;

public sealed class JsonSocialFeedProvider(
    ISocialProviderHttpClientFactory httpClientFactory,
    SocialProviderSettings settings,
    ISocialProviderCredentialSource credentialSource)
    : ISocialFeedSource, ISocialFeedProvider
{
    public async Task<Result<SocialProviderFeed, SocialSourceError>> FetchAsync(
        SocialFeedRequest request,
        CancellationToken cancellationToken)
    {
        if (!settings.Provider.Equals(
                request.Provider,
                StringComparison.OrdinalIgnoreCase))
        {
            return Result.Fail<SocialProviderFeed, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.Unavailable));
        }

        if (request.MaximumItems is < 1 or > SocialFeedLimits.MaximumItems)
        {
            return InvalidPayload();
        }

        var token = await credentialSource.GetBearerTokenAsync(
            settings.Provider,
            cancellationToken).ConfigureAwait(false);
        var responseResult = await httpClientFactory.SendGetAsync(
            settings,
            token,
            cancellationToken).ConfigureAwait(false);
        if (responseResult.IsFailure)
        {
            return Result.Fail<SocialProviderFeed, SocialSourceError>(responseResult.Error);
        }

        using var response = responseResult.Success;
        if (response.Content.Headers.ContentLength > settings.MaximumResponseBytes)
        {
            return InvalidPayload();
        }

        try
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (bounded.Length + read > settings.MaximumResponseBytes)
                {
                    return InvalidPayload();
                }

                await bounded.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            bounded.Position = 0;
            using var document = await JsonDocument.ParseAsync(
                    bounded,
                    DocumentOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array ||
                itemsElement.GetArrayLength() > request.MaximumItems)
            {
                return InvalidPayload();
            }

            var payload = document.RootElement.Deserialize<ProviderPayload>(JsonOptions);
            if (payload?.Items is null)
            {
                return InvalidPayload();
            }

            var items = payload.Items.Select(item => new SocialProviderItem(
                item.ExternalId ?? string.Empty,
                item.Text ?? string.Empty,
                ParseUri(item.SourceLink),
                ToMedia(item.Media),
                item.PublishedAtUtc)).ToArray();
            return Result.Succeed<SocialProviderFeed, SocialSourceError>(
                new SocialProviderFeed(settings.Provider, items, payload.FetchedAtUtc));
        }
        catch (JsonException)
        {
            return InvalidPayload();
        }
        catch (UriFormatException)
        {
            return InvalidPayload();
        }
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

    private static Uri? ParseUri(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : new Uri(value, UriKind.Absolute);

    private static SocialProviderMedia? ToMedia(ProviderMedia? media) =>
        media is null
            ? null
            : new SocialProviderMedia(
                media.Type,
                new Uri(media.Url ?? string.Empty, UriKind.Absolute),
                ParseUri(media.ThumbnailUrl),
                media.Width,
                media.Height,
                media.DurationSeconds,
                media.AltText,
                media.Caption);

    private static Result<SocialProviderFeed, SocialSourceError> InvalidPayload() =>
        Result.Fail<SocialProviderFeed, SocialSourceError>(
            new SocialSourceError(SocialSourceErrorKind.Malformed));

    private static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<SocialMediaType>());
        return options;
    }

    private sealed record ProviderPayload(
        DateTimeOffset FetchedAtUtc,
        IReadOnlyList<ProviderItem> Items);

    private sealed record ProviderItem(
        string? ExternalId,
        string? Text,
        string? SourceLink,
        ProviderMedia? Media,
        DateTimeOffset PublishedAtUtc);

    private sealed record ProviderMedia(
        SocialMediaType Type,
        string? Url,
        string? ThumbnailUrl,
        int? Width,
        int? Height,
        double? DurationSeconds,
        string? AltText,
        string? Caption);
}
