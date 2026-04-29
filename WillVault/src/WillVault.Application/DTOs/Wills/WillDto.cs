namespace WillVault.Application.DTOs.Wills;

public record WillDto(
    Guid Id,
    Guid OwnerId,
    string Title,
    string? Description,
    Guid? ExecutorId,
    string? LegalNotes,
    string? SpecialInstructions,
    bool IsArchived,
    int RecipientCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);
