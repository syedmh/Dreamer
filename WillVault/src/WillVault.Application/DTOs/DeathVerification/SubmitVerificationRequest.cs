namespace WillVault.Application.DTOs.DeathVerification;

public record SubmitVerificationRequest(
    Guid OwnerId,
    string? SubmitterNotes);
