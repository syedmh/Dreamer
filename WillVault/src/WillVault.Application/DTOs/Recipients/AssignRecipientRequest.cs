namespace WillVault.Application.DTOs.Recipients;

public record AssignRecipientRequest(
    Guid RecipientId,
    int DeliveryPriority,
    int? ScheduledDeliveryDelayMinutes);
