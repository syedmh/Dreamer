using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Messaging;

public interface IPushGateway
{
    ValueTask<PushDeliveryResult> SendAsync(PushMessage message, CancellationToken cancellationToken = default);
}

public interface IOutboxWriter
{
    ValueTask AddAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed record PushMessage
{
    public PushMessage(
        OrganizationId organizationId,
        MembershipId membershipId,
        string genericText,
        string resourceType,
        string resourceId)
    {
        OrganizationId = organizationId.EnsureValid();
        MembershipId = membershipId.EnsureValid();
        GenericText = Required(genericText);
        ResourceType = Required(resourceType);
        ResourceId = Required(resourceId);
    }

    public OrganizationId OrganizationId { get; }
    public MembershipId MembershipId { get; }
    public string GenericText { get; }
    public string ResourceType { get; }
    public string ResourceId { get; }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

public sealed record PushDeliveryResult(bool Accepted, string? ProviderReference, string? RejectionCode);

public sealed record OutboxMessage
{
    public OutboxMessage(
        OutboxMessageId id,
        OrganizationId organizationId,
        string type,
        string payload,
        DateTimeOffset occurredAt)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payload);
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    public OutboxMessageId Id { get; }
    public OrganizationId OrganizationId { get; }
    public string Type { get; }
    public string Payload { get; }
    public DateTimeOffset OccurredAt { get; }
}
