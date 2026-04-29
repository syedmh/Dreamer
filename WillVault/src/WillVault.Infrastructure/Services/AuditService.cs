using Microsoft.Extensions.Logging;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.Infrastructure.Services;

public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IAuditLogRepository auditLogRepository, ILogger<AuditService> logger)
    {
        _auditLogRepository = auditLogRepository;
        _logger = logger;
    }

    public async Task LogAsync(
        Guid? actorId,
        ActorType actorType,
        string action,
        string? entityType = null,
        Guid? entityId = null,
        string? details = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var auditLog = new AuditLog
        {
            ActorId = actorId,
            ActorType = actorType,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Reason = reason
        };

        await _auditLogRepository.AddAsync(auditLog, cancellationToken);
        _logger.LogInformation("Audit: {Action} by {ActorType} ({ActorId}) on {EntityType} ({EntityId})",
            action, actorType, actorId, entityType, entityId);
    }
}
