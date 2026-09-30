using System.Text.Json;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Identity;

namespace Husaynia.Infrastructure.Identity;

public sealed class EfAuditWriter(
    HusayniaIdentityDbContext dbContext,
    ISensitiveDataRedactor redactor,
    TimeProvider timeProvider) : IAuditWriter
{
    private readonly HusayniaIdentityDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ISensitiveDataRedactor redactor =
        redactor ?? throw new ArgumentNullException(nameof(redactor));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task AppendAsync(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(details);

        var safeDetails = redactor.Redact(details);
        var auditEvent = new AuditEvent(
            descriptor.ActorId,
            JsonSerializer.Serialize(descriptor.Roles.OrderBy(role => role, StringComparer.Ordinal)),
            descriptor.Action,
            descriptor.TargetType,
            descriptor.TargetId,
            outcome,
            descriptor.CorrelationId,
            timeProvider.GetUtcNow().ToUniversalTime(),
            JsonSerializer.Serialize(
                safeDetails.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)));
        dbContext.Add(auditEvent);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
