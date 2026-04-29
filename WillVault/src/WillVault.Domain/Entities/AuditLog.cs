using WillVault.Domain.Enums;

namespace WillVault.Domain.Entities;

/// <summary>
/// Append-only audit log. No UPDATE or DELETE operations allowed.
/// </summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorId { get; set; }
    public ActorType ActorType { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }

    /// <summary>
    /// JSON-serialized details about the action.
    /// </summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Reason { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
