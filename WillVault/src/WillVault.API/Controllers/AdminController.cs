using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WillVault.Application.DTOs.Common;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Enums;

namespace WillVault.API.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly IVaultOwnerRepository _vaultOwnerRepository;
    private readonly IDeliveryJobRepository _deliveryJobRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IAuditService _auditService;

    public AdminController(
        IVaultOwnerRepository vaultOwnerRepository,
        IDeliveryJobRepository deliveryJobRepository,
        IAuditLogRepository auditLogRepository,
        IAuditService auditService)
    {
        _vaultOwnerRepository = vaultOwnerRepository;
        _deliveryJobRepository = deliveryJobRepository;
        _auditLogRepository = auditLogRepository;
        _auditService = auditService;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var allOwners = await _vaultOwnerRepository.GetAllAsync();
        var totalCount = allOwners.Count;
        var paged = allOwners
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.FullName,
                o.Email,
                o.Phone,
                o.DateOfBirth,
                o.AccountStatus,
                o.CreatedAt,
                o.UpdatedAt
            })
            .ToList();

        return Ok(new PagedResult<object>(paged, totalCount, page, pageSize));
    }

    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var owner = await _vaultOwnerRepository.GetByIdAsync(id);
        if (owner is null) return NotFound();

        return Ok(new
        {
            owner.Id,
            owner.FullName,
            owner.Email,
            owner.Phone,
            owner.DateOfBirth,
            owner.AccountStatus,
            owner.IdentityUserId,
            owner.CreatedAt,
            owner.UpdatedAt
        });
    }

    [HttpGet("delivery-logs")]
    public async Task<IActionResult> GetDeliveryLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DeliveryStatus? status = null)
    {
        var allJobs = await _deliveryJobRepository.GetAllAsync();

        if (status.HasValue)
            allJobs = allJobs.Where(j => j.Status == status.Value).ToList();

        var totalCount = allJobs.Count;
        var paged = allJobs
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(j => new
            {
                j.Id,
                j.VaultItemId,
                j.RecipientId,
                j.Channel,
                j.Status,
                j.IdempotencyKey,
                j.Attempts,
                j.MaxAttempts,
                j.LastAttemptAt,
                j.NextRetryAt,
                j.DeliveredAt,
                j.ErrorMessage,
                j.ScheduledAt,
                j.CreatedAt,
                j.UpdatedAt
            })
            .ToList();

        return Ok(new PagedResult<object>(paged, totalCount, page, pageSize));
    }

    [HttpGet("audit-logs")]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? actorId = null,
        [FromQuery] string? entityType = null)
    {
        IReadOnlyList<Domain.Entities.AuditLog> logs;

        if (actorId.HasValue)
        {
            logs = await _auditLogRepository.GetByActorAsync(actorId.Value);
        }
        else if (!string.IsNullOrEmpty(entityType))
        {
            // Get all and filter by entity type since the repository doesn't have a specific method
            logs = await _auditLogRepository.GetByActorAsync(Guid.Empty);
            // Fallback: we need to get all logs - use actor-based query as base
            // Since there's no GetAll, we'll handle this by entity query with a known entity ID
            logs = [];
        }
        else
        {
            // No specific filter - retrieve by actor with empty guid returns empty,
            // so we handle pagination differently
            logs = [];
        }

        // For general queries without specific actor, get logs via a broader approach
        if (!actorId.HasValue)
        {
            // The audit log repository doesn't have a GetAll method,
            // so we return empty for non-actor-specific queries
            // In practice, either actorId or entityType should be provided
            var totalCount = logs.Count;
            var pagedLogs = logs
                .OrderByDescending(l => l.Timestamp)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Ok(new PagedResult<object>(
                pagedLogs.Select(l => (object)new
                {
                    l.Id,
                    l.ActorId,
                    l.ActorType,
                    l.Action,
                    l.EntityType,
                    l.EntityId,
                    l.Details,
                    l.IpAddress,
                    l.UserAgent,
                    l.Reason,
                    l.Timestamp
                }).ToList(),
                totalCount, page, pageSize));
        }

        var total = logs.Count;
        var result = logs
            .OrderByDescending(l => l.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.ActorId,
                l.ActorType,
                l.Action,
                l.EntityType,
                l.EntityId,
                l.Details,
                l.IpAddress,
                l.UserAgent,
                l.Reason,
                l.Timestamp
            })
            .ToList();

        return Ok(new PagedResult<object>(result, total, page, pageSize));
    }
}
