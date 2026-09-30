using System.Text.Json;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Persistence.Core;

namespace Husaynia.Infrastructure.Prayer;

public sealed class PrayerTransactionalAuditAppender(
    HusayniaDbContext dbContext,
    ISensitiveDataRedactor redactor,
    TimeProvider timeProvider)
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ISensitiveDataRedactor redactor =
        redactor ?? throw new ArgumentNullException(nameof(redactor));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public void Append(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(details);
        var safeDetails = redactor.Redact(details);
        dbContext.Add(new AuditEvent(
            descriptor.ActorId,
            JsonSerializer.Serialize(
                descriptor.Roles.OrderBy(role => role, StringComparer.Ordinal)),
            descriptor.Action,
            descriptor.TargetType,
            descriptor.TargetId,
            outcome,
            descriptor.CorrelationId,
            timeProvider.GetUtcNow().ToUniversalTime(),
            JsonSerializer.Serialize(
                safeDetails
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.Ordinal))));
    }
}
