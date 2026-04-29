namespace WillVault.Application.DTOs.Recipients;

public record RecipientDto(
    Guid Id,
    Guid OwnerId,
    string FullName,
    string Email,
    string? Phone,
    string? Relationship,
    bool IsEmailVerified,
    bool IsPhoneVerified,
    DateTime? EmailVerifiedAt,
    DateTime? PhoneVerifiedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);
