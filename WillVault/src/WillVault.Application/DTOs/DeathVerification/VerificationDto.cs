using WillVault.Domain.Enums;

namespace WillVault.Application.DTOs.DeathVerification;

public record VerificationDto(
    Guid Id,
    Guid OwnerId,
    Guid SubmittedById,
    DateTime SubmittedAt,
    string? SubmitterNotes,
    VerificationStatus Status,
    Guid? ReviewedById,
    DateTime? ReviewedAt,
    string? ReviewerNotes,
    bool RequiresSecondApproval,
    Guid? SecondReviewerId,
    DateTime? SecondReviewedAt,
    string? SecondReviewerNotes,
    DateTime? CooldownExpiresAt,
    string? RejectionReason,
    string? DisputeReason,
    Guid? DisputedById,
    DateTime CreatedAt,
    DateTime UpdatedAt);
