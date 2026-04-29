namespace WillVault.Application.DTOs.Wills;

public record CreateWillRequest(
    string Title,
    string? Description,
    Guid? ExecutorId,
    string? LegalNotes,
    string? SpecialInstructions);
