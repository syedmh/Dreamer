namespace Husaynia.Application.Contracts;

public interface IPrayerSource
{
    Task<Result<PrayerSourceSnapshot, IntegrationError>> GetAsync(
        PrayerSourceRequest request,
        CancellationToken ct);
}

public interface ISocialFeedProvider
{
    Task<Result<SocialFeedSnapshot, IntegrationError>> FetchAsync(
        SocialFeedRequest request,
        CancellationToken ct);
}

public interface IMediaStore
{
    Task<Result<MediaWriteReceipt, IntegrationError>> PutAsync(
        MediaWriteRequest request,
        CancellationToken ct);

    Task<Result<MediaReadResult, IntegrationError>> OpenReadAsync(
        MediaReadRequest request,
        CancellationToken ct);
}

public interface IUploadSafetyValidator
{
    Task<Result<UploadValidation, IntegrationError>> ValidateAsync(
        UploadCandidate candidate,
        CancellationToken ct);
}

public interface IOutboundMessageSender
{
    Task<Result<OutboundMessageReceipt, IntegrationError>> SendAsync(
        OutboundMessage message,
        CancellationToken ct);
}

public interface IPaymentGateway
{
    Task<Result<PaymentCheckout, IntegrationError>> CreateCheckoutAsync(
        PaymentCheckoutRequest request,
        string idempotencyKey,
        CancellationToken ct);

    Task<Result<VerifiedWebhook, IntegrationError>> VerifyWebhookAsync(
        ReadOnlyMemory<byte> body,
        string signature,
        CancellationToken ct);
}

public interface IJobLeaseStore
{
    Task<Result<JobLease?, IntegrationError>> TryAcquireAsync(
        JobKey key,
        WorkerIdentity worker,
        TimeSpan lease,
        CancellationToken ct);
}

public sealed record PrayerSourceRequest(YearMonth Month, string TimeZoneId, string ProfileHash);

public sealed record PrayerSourceSnapshot(PrayerSchedule Schedule, string Source);

public sealed record SocialFeedRequest(string Provider, int MaximumItems);

public sealed record SocialFeedItem(string ExternalId, string Text, Uri? Link, DateTimeOffset PublishedAtUtc);

public sealed record SocialFeedSnapshot(
    string Provider,
    IReadOnlyList<SocialFeedItem> Items,
    DateTimeOffset FetchedAtUtc);

public sealed record MediaWriteRequest(
    string StableKey,
    Stream Content,
    string ContentType,
    long Length,
    string Sha256);

public sealed record MediaWriteReceipt(string StorageKey, string ETag);

public sealed record MediaReadRequest(string StorageKey, long? Offset = null, long? Length = null);

public sealed record MediaReadResult(Stream Content, string ContentType, long Length, string ETag);

public sealed record UploadCandidate(string FileName, string ClaimedContentType, Stream Content, long Length);

public sealed record UploadValidation(bool IsSafe, string? DetectedContentType, IReadOnlyList<string> Errors);

public sealed record OutboundMessage(string DestinationKey, string TemplateKey, IReadOnlyDictionary<string, string> Values);

public sealed record OutboundMessageReceipt(string ProviderMessageId, DateTimeOffset AcceptedAtUtc);

public sealed record PaymentCheckoutRequest(DonationIntent Intent, Uri SuccessUri, Uri CancelUri);

public sealed record PaymentCheckout(Uri Location, string ProviderCheckoutId);

public sealed record VerifiedWebhook(string ProviderEventId, string EventType, ReadOnlyMemory<byte> Payload);

public sealed record JobLease(JobKey Key, WorkerIdentity Worker, DateTimeOffset ExpiresAtUtc);
