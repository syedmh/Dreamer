namespace WillVault.Application.DTOs.Recipients;

public record CreateRecipientRequest(
    string FullName,
    string Email,
    string? Phone,
    string? Relationship);
