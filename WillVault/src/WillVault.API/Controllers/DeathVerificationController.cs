using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.DeathVerification;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/death-verification")]
public class DeathVerificationController : ControllerBase
{
    private readonly IDeathVerificationRepository _verificationRepository;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly ITrustedContactRepository _trustedContactRepository;
    private readonly IStorageProvider _storageProvider;
    private readonly IAuditService _auditService;
    private readonly IConfiguration _configuration;

    public DeathVerificationController(
        IDeathVerificationRepository verificationRepository,
        IVaultOwnerRepository vaultOwnerRepository,
        ITrustedContactRepository trustedContactRepository,
        IStorageProvider storageProvider,
        IAuditService auditService,
        IConfiguration configuration)
    {
        _verificationRepository = verificationRepository;
        _vaultOwnerRepository = vaultOwnerRepository;
        _trustedContactRepository = trustedContactRepository;
        _storageProvider = storageProvider;
        _auditService = auditService;
        _configuration = configuration;
    }

    [HttpPost("submit")]
    [AllowAnonymous]
    public async Task<IActionResult> Submit(
        [FromForm] SubmitVerificationRequest request,
        [FromForm] List<IFormFile> deathCertificates)
    {
        var owner = await _vaultOwnerRepository.GetByIdAsync(request.OwnerId);
        if (owner is null)
            return NotFound(new ProblemDetails { Title = "Vault owner not found." });

        var trustedContacts = await _trustedContactRepository.GetByOwnerIdAsync(request.OwnerId);
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");

        TrustedContact? submitter = null;
        if (userId is not null)
        {
            var submitterOwner = await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
            if (submitterOwner is not null)
            {
                submitter = trustedContacts.FirstOrDefault(tc =>
                    tc.Email.Equals(submitterOwner.Email, StringComparison.OrdinalIgnoreCase) && tc.IsVerified);
            }
        }

        // Also allow matching by email in the trusted contacts list for anonymous submitters
        submitter ??= trustedContacts.FirstOrDefault(tc => tc.IsVerified);

        if (submitter is null)
            return BadRequest(new ProblemDetails { Title = "Submitter must be a verified trusted contact for this owner." });

        // Rate limit: reject if an active request already exists
        var activeRequest = await _verificationRepository.GetActiveByOwnerIdAsync(request.OwnerId);
        if (activeRequest is not null)
            return Conflict(new ProblemDetails { Title = "An active verification request already exists for this owner." });

        // Upload death certificate documents
        var documentPaths = new List<string>();
        foreach (var file in deathCertificates)
        {
            using var stream = file.OpenReadStream();
            var path = await _storageProvider.UploadAsync(stream, file.FileName, file.ContentType);
            documentPaths.Add(path);
        }

        var verification = new DeathVerificationRequest
        {
            OwnerId = request.OwnerId,
            SubmittedById = submitter.Id,
            SubmitterNotes = request.SubmitterNotes,
            DocumentPaths = string.Join(",", documentPaths),
            Status = VerificationStatus.Pending
        };

        await _verificationRepository.AddAsync(verification);

        owner.TransitionTo(AccountStatus.VerificationPending);
        await _vaultOwnerRepository.UpdateAsync(owner);

        await _auditService.LogAsync(
            submitter.Id, ActorType.TrustedContact, "DeathVerificationSubmitted",
            "DeathVerificationRequest", verification.Id);

        return CreatedAtAction(nameof(GetById), new { id = verification.Id }, MapToDto(verification));
    }

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetPending()
    {
        var requests = await _verificationRepository.GetPendingAsync();
        var dtos = requests.Select(MapToDto).ToList();
        return Ok(dtos);
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var verification = await _verificationRepository.GetByIdAsync(id);
        if (verification is null) return NotFound();
        return Ok(MapToDto(verification));
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ReviewVerificationRequest request)
    {
        var verification = await _verificationRepository.GetByIdAsync(id);
        if (verification is null) return NotFound();

        if (verification.Status != VerificationStatus.Pending && verification.Status != VerificationStatus.UnderReview)
            return BadRequest(new ProblemDetails { Title = "Verification request is not in a reviewable state." });

        var adminOwner = await GetCurrentOwnerAsync();
        var adminId = adminOwner?.Id ?? Guid.Empty;

        verification.ReviewedById = adminId;
        verification.ReviewedAt = DateTime.UtcNow;
        verification.ReviewerNotes = request.Notes;
        verification.Status = VerificationStatus.UnderReview;

        if (!verification.RequiresSecondApproval)
        {
            var cooldownHours = _configuration.GetValue<int>("DeathVerification:CooldownHours", 48);
            verification.Status = VerificationStatus.Approved;
            verification.CooldownExpiresAt = DateTime.UtcNow.AddHours(cooldownHours);

            var owner = await _vaultOwnerRepository.GetByIdAsync(verification.OwnerId);
            if (owner is not null)
            {
                owner.TransitionTo(AccountStatus.Verified);
                await _vaultOwnerRepository.UpdateAsync(owner);
            }
        }

        await _verificationRepository.UpdateAsync(verification);

        await _auditService.LogAsync(
            adminId, ActorType.Admin, "DeathVerificationApproved",
            "DeathVerificationRequest", verification.Id,
            reason: request.Notes);

        return Ok(MapToDto(verification));
    }

    [HttpPost("{id:guid}/second-approve")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SecondApprove(Guid id, [FromBody] ReviewVerificationRequest request)
    {
        var verification = await _verificationRepository.GetByIdAsync(id);
        if (verification is null) return NotFound();

        if (verification.Status != VerificationStatus.UnderReview)
            return BadRequest(new ProblemDetails { Title = "Verification request is not awaiting second approval." });

        if (!verification.RequiresSecondApproval)
            return BadRequest(new ProblemDetails { Title = "This verification does not require second approval." });

        if (!verification.ReviewedById.HasValue)
            return BadRequest(new ProblemDetails { Title = "First approval has not been completed." });

        var adminOwner = await GetCurrentOwnerAsync();
        var adminId = adminOwner?.Id ?? Guid.Empty;

        if (verification.ReviewedById == adminId)
            return BadRequest(new ProblemDetails { Title = "Second reviewer must be a different admin than the first reviewer." });

        verification.SecondReviewerId = adminId;
        verification.SecondReviewedAt = DateTime.UtcNow;
        verification.SecondReviewerNotes = request.Notes;

        var cooldownHours = _configuration.GetValue<int>("DeathVerification:CooldownHours", 48);
        verification.Status = VerificationStatus.Approved;
        verification.CooldownExpiresAt = DateTime.UtcNow.AddHours(cooldownHours);

        await _verificationRepository.UpdateAsync(verification);

        var owner = await _vaultOwnerRepository.GetByIdAsync(verification.OwnerId);
        if (owner is not null)
        {
            owner.TransitionTo(AccountStatus.Verified);
            await _vaultOwnerRepository.UpdateAsync(owner);
        }

        await _auditService.LogAsync(
            adminId, ActorType.Admin, "DeathVerificationSecondApproved",
            "DeathVerificationRequest", verification.Id,
            reason: request.Notes);

        return Ok(MapToDto(verification));
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] ReviewVerificationRequest request)
    {
        var verification = await _verificationRepository.GetByIdAsync(id);
        if (verification is null) return NotFound();

        if (verification.Status != VerificationStatus.Pending &&
            verification.Status != VerificationStatus.UnderReview)
            return BadRequest(new ProblemDetails { Title = "Verification request is not in a rejectable state." });

        var adminOwner = await GetCurrentOwnerAsync();
        var adminId = adminOwner?.Id ?? Guid.Empty;

        verification.Status = VerificationStatus.Rejected;
        verification.RejectionReason = request.Notes;

        await _verificationRepository.UpdateAsync(verification);

        var owner = await _vaultOwnerRepository.GetByIdAsync(verification.OwnerId);
        if (owner is not null && owner.AccountStatus == AccountStatus.VerificationPending)
        {
            owner.TransitionTo(AccountStatus.Active);
            await _vaultOwnerRepository.UpdateAsync(owner);
        }

        await _auditService.LogAsync(
            adminId, ActorType.Admin, "DeathVerificationRejected",
            "DeathVerificationRequest", verification.Id,
            reason: request.Notes);

        return Ok(MapToDto(verification));
    }

    [HttpPost("{id:guid}/dispute")]
    [Authorize]
    public async Task<IActionResult> Dispute(Guid id, [FromBody] ReviewVerificationRequest request)
    {
        var verification = await _verificationRepository.GetByIdAsync(id);
        if (verification is null) return NotFound();

        if (verification.Status != VerificationStatus.Approved)
            return BadRequest(new ProblemDetails { Title = "Only approved verifications can be disputed." });

        var disputeOwner = await GetCurrentOwnerAsync();
        var disputeId = disputeOwner?.Id ?? Guid.Empty;

        verification.Status = VerificationStatus.Disputed;
        verification.DisputeReason = request.Notes;
        verification.DisputedById = disputeId;

        await _verificationRepository.UpdateAsync(verification);

        var owner = await _vaultOwnerRepository.GetByIdAsync(verification.OwnerId);
        if (owner is not null)
        {
            if (owner.CanTransitionTo(AccountStatus.VerificationPending))
            {
                owner.TransitionTo(AccountStatus.VerificationPending);
                await _vaultOwnerRepository.UpdateAsync(owner);
            }
        }

        await _auditService.LogAsync(
            disputeId, ActorType.Owner, "DeathVerificationDisputed",
            "DeathVerificationRequest", verification.Id,
            reason: request.Notes);

        return Ok(MapToDto(verification));
    }

    private async Task<VaultOwner?> GetCurrentOwnerAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");
        if (userId is null) return null;
        return await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
    }

    private static VerificationDto MapToDto(DeathVerificationRequest v) => new(
        v.Id, v.OwnerId, v.SubmittedById, v.SubmittedAt, v.SubmitterNotes,
        v.Status, v.ReviewedById, v.ReviewedAt, v.ReviewerNotes,
        v.RequiresSecondApproval, v.SecondReviewerId, v.SecondReviewedAt,
        v.SecondReviewerNotes, v.CooldownExpiresAt, v.RejectionReason,
        v.DisputeReason, v.DisputedById, v.CreatedAt, v.UpdatedAt);
}
