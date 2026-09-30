using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Persistence.Core;

internal sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
    }

    internal IdempotencyRecord(string scope, string key, string requestHash)
    {
        Id = Guid.NewGuid();
        Scope = scope;
        Key = key;
        RequestHash = requestHash;
    }

    public Guid Id { get; private set; }

    public string Scope { get; private set; } = string.Empty;

    public string Key { get; private set; } = string.Empty;

    public string RequestHash { get; private set; } = string.Empty;

    public string? Receipt { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    internal void Complete(string receipt)
    {
        Receipt = receipt;
        CompletedAtUtc = DateTimeOffset.UtcNow;
    }
}

internal sealed class IdempotencyRecordConfiguration
    : MutableEntityConfiguration<IdempotencyRecord>
{
    protected override void ConfigureMutableEntity(
        EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("PersistenceIdempotency");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.Scope).HasMaxLength(100).IsRequired();
        builder.Property(record => record.Key).HasMaxLength(256).IsRequired();
        builder.Property(record => record.RequestHash).HasMaxLength(128).IsRequired();
        builder.Property(record => record.Receipt).HasMaxLength(4000);
        builder.HasIndex(record => new { record.Scope, record.Key }).IsUnique();
    }
}
