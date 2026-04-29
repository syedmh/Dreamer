using WillVault.Domain.Enums;

namespace WillVault.Application.DTOs.Delivery;

public record DeliveryJobDto(
    Guid Id,
    Guid VaultItemId,
    Guid RecipientId,
    DeliveryChannel Channel,
    DeliveryStatus Status,
    string IdempotencyKey,
    int Attempts,
    int MaxAttempts,
    DateTime? LastAttemptAt,
    DateTime? NextRetryAt,
    DateTime? DeliveredAt,
    string? ErrorMessage,
    DateTime ScheduledAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
