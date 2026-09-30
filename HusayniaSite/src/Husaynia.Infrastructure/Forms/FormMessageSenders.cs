using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;

namespace Husaynia.Infrastructure.Forms;

public sealed class DisabledFormMessageSender : IOutboundMessageSender
{
    public Task<Result<OutboundMessageReceipt, IntegrationError>> SendAsync(
        OutboundMessage message,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Task.FromResult(
            Result.Fail<OutboundMessageReceipt, IntegrationError>(
                new IntegrationError(
                    "sender_disabled",
                    "Outbound form delivery is disabled.")));
    }
}

public sealed class PickupFormMessageSender(
    FormsOptions options,
    TimeProvider timeProvider) : IOutboundMessageSender
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = false };
    private readonly FormsOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<OutboundMessageReceipt, IntegrationError>> SendAsync(
        OutboundMessage message,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (options.DeliveryMode != FormDeliveryMode.Pickup ||
            !string.Equals(
                message.DestinationKey,
                options.PickupDestinationKey,
                StringComparison.Ordinal))
        {
            return Result.Fail<OutboundMessageReceipt, IntegrationError>(
                new IntegrationError(
                    "destination_not_configured",
                    "The non-public pickup destination is not configured."));
        }

        try
        {
            Directory.CreateDirectory(options.PickupDirectory);
            var pickupId = Guid.NewGuid().ToString("N");
            var path = Path.Combine(options.PickupDirectory, $"{pickupId}.json");
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(
                    stream,
                    new PickupMessage(
                        message.DestinationKey,
                        message.TemplateKey,
                        message.Values,
                        timeProvider.GetUtcNow().ToUniversalTime()),
                    SerializerOptions,
                    ct)
                .ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            return Result.Succeed<OutboundMessageReceipt, IntegrationError>(
                new OutboundMessageReceipt(
                    pickupId,
                    timeProvider.GetUtcNow().ToUniversalTime()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return Result.Fail<OutboundMessageReceipt, IntegrationError>(
                new IntegrationError(
                    "pickup_unavailable",
                    "The non-public pickup sink is unavailable."));
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Fail<OutboundMessageReceipt, IntegrationError>(
                new IntegrationError(
                    "pickup_unavailable",
                    "The non-public pickup sink is unavailable."));
        }
    }

    private sealed record PickupMessage(
        string DestinationKey,
        string TemplateKey,
        IReadOnlyDictionary<string, string> Values,
        DateTimeOffset PickedUpAtUtc);
}
