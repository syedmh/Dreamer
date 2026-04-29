using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.Delivery;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/delivery")]
[Authorize]
public class DeliveryController : ControllerBase
{
    private readonly IDeliveryJobRepository _deliveryJobRepository;
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly INotificationService _notificationService;
    private readonly IAuditService _auditService;

    public DeliveryController(
        IDeliveryJobRepository deliveryJobRepository,
        IVaultOwnerRepository vaultOwnerRepository,
        INotificationService notificationService,
        IAuditService auditService)
    {
        _deliveryJobRepository = deliveryJobRepository;
        _vaultOwnerRepository = vaultOwnerRepository;
        _notificationService = notificationService;
        _auditService = auditService;
    }

    [HttpGet("status/{ownerId:guid}")]
    public async Task<IActionResult> GetStatus(Guid ownerId)
    {
        var currentOwner = await GetCurrentOwnerAsync();
        if (currentOwner is null) return Unauthorized();

        // Allow access if the user is the owner or is an admin
        if (currentOwner.Id != ownerId && !User.IsInRole("Admin"))
            return Forbid();

        var jobs = await _deliveryJobRepository.GetByOwnerIdAsync(ownerId);
        var dtos = jobs.Select(MapToDto).ToList();
        return Ok(dtos);
    }

    [HttpPost("retry/{jobId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Retry(Guid jobId)
    {
        var job = await _deliveryJobRepository.GetByIdAsync(jobId);
        if (job is null) return NotFound();

        if (!job.CanRetry)
            return BadRequest(new ProblemDetails { Title = "This job is not eligible for retry." });

        job.Status = DeliveryStatus.Pending;
        job.NextRetryAt = null;
        job.ScheduledAt = DateTime.UtcNow;

        await _deliveryJobRepository.UpdateAsync(job);

        var adminOwner = await GetCurrentOwnerAsync();
        await _auditService.LogAsync(
            adminOwner?.Id, ActorType.Admin, "DeliveryJobRetried",
            "DeliveryJob", job.Id);

        return Ok(MapToDto(job));
    }

    private async Task<VaultOwner?> GetCurrentOwnerAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? User.FindFirstValue("sub");
        if (userId is null) return null;
        return await _vaultOwnerRepository.GetByIdentityUserIdAsync(userId);
    }

    private static DeliveryJobDto MapToDto(DeliveryJob j) => new(
        j.Id, j.VaultItemId, j.RecipientId, j.Channel, j.Status,
        j.IdempotencyKey, j.Attempts, j.MaxAttempts, j.LastAttemptAt,
        j.NextRetryAt, j.DeliveredAt, j.ErrorMessage, j.ScheduledAt,
        j.CreatedAt, j.UpdatedAt);
}
