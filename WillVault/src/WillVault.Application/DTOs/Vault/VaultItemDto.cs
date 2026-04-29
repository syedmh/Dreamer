using WillVault.Domain.Enums;

namespace WillVault.Application.DTOs.Vault;

public record VaultItemDto(
    Guid Id,
    Guid OwnerId,
    string Title,
    string? Description,
    VaultItemType ItemType,
    string? OriginalFileName,
    string? ContentType,
    long? FileSizeBytes,
    bool IsArchived,
    Guid? ExecutorId,
    string? LegalNotes,
    string? SpecialInstructions,
    int RecipientCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);
