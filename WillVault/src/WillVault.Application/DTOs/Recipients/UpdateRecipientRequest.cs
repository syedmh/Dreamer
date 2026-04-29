namespace WillVault.Application.DTOs.Recipients;

public record UpdateRecipientRequest(
    string FullName,
    string Email,
    string? Phone,
    string? Relationship);
