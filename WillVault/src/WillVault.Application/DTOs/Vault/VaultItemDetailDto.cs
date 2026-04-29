using WillVault.Application.DTOs.Recipients;
using WillVault.Domain.Enums;

namespace WillVault.Application.DTOs.Vault;

public record VaultItemDetailDto(
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
    IReadOnlyList<RecipientDto> Recipients,
    DateTime CreatedAt,
    DateTime UpdatedAt);
