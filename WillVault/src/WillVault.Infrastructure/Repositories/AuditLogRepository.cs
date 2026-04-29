using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class AuditLogRepository : IAuditLogRepository
{
    private readonly WillVaultDbContext _context;

    public AuditLogRepository(WillVaultDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default)
    {
        await _context.AuditLogs.AddAsync(auditLog, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLog>> GetByEntityAsync(
        string entityType, Guid entityId, CancellationToken cancellationToken = default)
    {
        return await _context.AuditLogs
            .Where(a => a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditLog>> GetByActorAsync(
        Guid actorId, CancellationToken cancellationToken = default)
    {
        return await _context.AuditLogs
            .Where(a => a.ActorId == actorId)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }
}
