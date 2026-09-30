using System.Text.Json;

namespace Husaynia.Domain.Identity;

public enum PrivilegedAttemptOutcome
{
    Allowed = 0,
    Denied = 1,
}

public sealed class AuditEvent
{
    private const int ActorIdLength = 256;
    private const int RolesJsonLength = 2_000;
    private const int ActionLength = 100;
    private const int TargetTypeLength = 100;
    private const int TargetIdLength = 256;
    private const int OutcomeLength = 32;
    private const int CorrelationIdLength = 128;
    private const int DetailJsonLength = 4_000;

    private AuditEvent()
    {
    }

    public AuditEvent(
        string? actorId,
        string rolesJson,
        string action,
        string targetType,
        string? targetId,
        PrivilegedAttemptOutcome outcome,
        string correlationId,
        DateTimeOffset occurredAtUtc,
        string detailJson)
    {
        Id = Guid.NewGuid();
        ActorId = BoundedOrNull(actorId, ActorIdLength, nameof(actorId));
        RolesJson = RequireBoundedJson(rolesJson, RolesJsonLength, nameof(rolesJson));
        Action = RequireBounded(action, ActionLength, nameof(action));
        TargetType = RequireBounded(targetType, TargetTypeLength, nameof(targetType));
        TargetId = BoundedOrNull(targetId, TargetIdLength, nameof(targetId));
        Outcome = outcome.ToString().ToLowerInvariant();
        CorrelationId = RequireBounded(correlationId, CorrelationIdLength, nameof(correlationId));
        OccurredAtUtc = occurredAtUtc.Offset == TimeSpan.Zero
            ? occurredAtUtc
            : occurredAtUtc.ToUniversalTime();
        DetailJson = RequireBoundedJson(detailJson, DetailJsonLength, nameof(detailJson));
    }

    public Guid Id { get; private set; }

    public string? ActorId { get; private set; }

    public string RolesJson { get; private set; } = "[]";

    public string Action { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    public string Outcome { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string DetailJson { get; private set; } = "{}";

    private static string RequireBounded(string value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", paramName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }

        return normalized;
    }

    private static string? BoundedOrNull(string? value, int maximumLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }

        return normalized;
    }

    private static string RequireBoundedJson(string value, int maximumLength, string paramName)
    {
        var normalized = RequireBounded(value, maximumLength, paramName);
        using var _ = JsonDocument.Parse(normalized);
        return normalized;
    }
}
