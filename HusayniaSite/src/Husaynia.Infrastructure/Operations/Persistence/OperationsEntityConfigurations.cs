using Husaynia.Application.Operations.Retention;
using Husaynia.Domain.Operations.Jobs;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Operations.Persistence;

internal sealed class JobDefinitionConfiguration : MutableEntityConfiguration<JobDefinition>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<JobDefinition> builder)
    {
        builder.ToTable("OperationsJobDefinitions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Key).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.HandlerName).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.InitialBackoff).HasPrecision(7);
        builder.Property(entity => entity.MaximumBackoff).HasPrecision(7);
        builder.HasIndex(entity => entity.Key).IsUnique();
    }
}

internal sealed class JobInstanceConfiguration : MutableEntityConfiguration<JobInstance>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<JobInstance> builder)
    {
        builder.ToTable("OperationsJobInstances");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.PayloadJson).HasMaxLength(64_000).IsRequired();
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.LeaseOwner).HasMaxLength(200);
        builder.Property(entity => entity.LastErrorCode).HasMaxLength(200);
        builder.HasIndex(entity => new { entity.DefinitionId, entity.IdempotencyKey }).IsUnique();
        builder.HasIndex(entity => new { entity.State, entity.NextRunAtUtc, entity.LeaseExpiresAtUtc });
        builder.HasOne<JobDefinition>()
            .WithMany()
            .HasForeignKey(entity => entity.DefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class JobAttemptConfiguration : MutableEntityConfiguration<JobAttempt>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<JobAttempt> builder)
    {
        builder.ToTable("OperationsJobAttempts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Worker).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.ErrorCode).HasMaxLength(200);
        builder.HasIndex(entity => new { entity.JobInstanceId, entity.Number }).IsUnique();
        builder.HasIndex(entity => entity.LeaseToken).IsUnique();
        builder.HasOne<JobInstance>()
            .WithMany()
            .HasForeignKey(entity => entity.JobInstanceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JobOperationalAuditConfiguration
    : IEntityTypeConfiguration<JobOperationalAudit>
{
    public void Configure(EntityTypeBuilder<JobOperationalAudit> builder)
    {
        builder.ToTable("OperationsJobAudit");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Action).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.Actor).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Reason).HasMaxLength(1_000).IsRequired();
        builder.Property(entity => entity.CorrelationId).HasMaxLength(128).IsRequired();
        builder.HasIndex(entity => entity.JobInstanceId);
    }
}

internal sealed class RetentionHoldRecord
{
    private RetentionHoldRecord()
    {
    }

    internal RetentionHoldRecord(
        string idempotencyKey,
        string target,
        string subjectId,
        string kind,
        string actor,
        string reason,
        DateTimeOffset startsAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        Id = Guid.NewGuid();
        IdempotencyKey = idempotencyKey;
        Target = target;
        SubjectId = subjectId;
        Kind = kind;
        Actor = actor;
        Reason = reason;
        StartsAtUtc = startsAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid Id { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string Target { get; private set; } = string.Empty;

    public string SubjectId { get; private set; } = string.Empty;

    public string Kind { get; private set; } = string.Empty;

    public string Actor { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public DateTimeOffset StartsAtUtc { get; private set; }

    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public string? ReleasedBy { get; private set; }

    public string? ReleaseReason { get; private set; }

    internal void Release(string actor, string reason, DateTimeOffset now)
    {
        ReleasedAtUtc ??= now;
        ReleasedBy ??= actor;
        ReleaseReason ??= reason;
    }
}

internal sealed class RetentionRunRecord
{
    private RetentionRunRecord()
    {
    }

    internal RetentionRunRecord(
        string idempotencyKey,
        string policy,
        string target,
        string mode,
        string actor,
        string correlationId,
        DateTimeOffset startedAtUtc)
    {
        Id = Guid.NewGuid();
        IdempotencyKey = idempotencyKey;
        Policy = policy;
        Target = target;
        Mode = mode;
        Actor = actor;
        CorrelationId = correlationId;
        StartedAtUtc = startedAtUtc;
        LeaseToken = Guid.NewGuid();
    }

    public Guid Id { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string Policy { get; private set; } = string.Empty;

    public string Target { get; private set; } = string.Empty;

    public string Mode { get; private set; } = string.Empty;

    public string Actor { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; private set; }

    public Guid LeaseToken { get; private set; }

    public DateTimeOffset LeaseExpiresAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public int? Examined { get; private set; }

    public int? Held { get; private set; }

    public int? Applied { get; private set; }

    internal void StartLease(DateTimeOffset now, TimeSpan duration)
    {
        LeaseToken = Guid.NewGuid();
        LeaseExpiresAtUtc = now.Add(duration);
    }

    internal bool HasLease(Guid leaseToken, DateTimeOffset now) =>
        LeaseToken == leaseToken && LeaseExpiresAtUtc > now && !CompletedAtUtc.HasValue;

    internal void Complete(int examined, int held, int applied, DateTimeOffset now)
    {
        Examined = examined;
        Held = held;
        Applied = applied;
        CompletedAtUtc = now;
    }
}

internal sealed class RetentionRunItemRecord
{
    private RetentionRunItemRecord()
    {
    }

    internal RetentionRunItemRecord(
        Guid runId,
        string target,
        string subjectId,
        DateTimeOffset eligibleAtUtc)
    {
        Id = Guid.NewGuid();
        RunId = runId;
        Target = target;
        SubjectId = subjectId;
        EligibleAtUtc = eligibleAtUtc;
        Status = RetentionBatchItemStatus.Pending.ToString();
    }

    public Guid Id { get; private set; }

    public Guid RunId { get; private set; }

    public string Target { get; private set; } = string.Empty;

    public string SubjectId { get; private set; } = string.Empty;

    public DateTimeOffset EligibleAtUtc { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    internal void MarkHeld(DateTimeOffset now)
    {
        Status = RetentionBatchItemStatus.Held.ToString();
        UpdatedAtUtc = now;
    }

    internal void MarkApplying(DateTimeOffset now)
    {
        Status = RetentionBatchItemStatus.Applying.ToString();
        UpdatedAtUtc = now;
    }

    internal void MarkApplied(DateTimeOffset now)
    {
        Status = RetentionBatchItemStatus.Applied.ToString();
        UpdatedAtUtc = now;
    }
}

internal sealed class RetentionHoldRecordConfiguration
    : IEntityTypeConfiguration<RetentionHoldRecord>
{
    public void Configure(EntityTypeBuilder<RetentionHoldRecord> builder)
    {
        builder.ToTable("OperationsRetentionHolds");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Target).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.SubjectId).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Kind).HasMaxLength(50).IsRequired();
        builder.Property(entity => entity.Actor).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.Reason).HasMaxLength(1_000).IsRequired();
        builder.Property(entity => entity.ReleasedBy).HasMaxLength(200);
        builder.Property(entity => entity.ReleaseReason).HasMaxLength(1_000);
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
        builder.HasIndex(entity => new { entity.Target, entity.SubjectId, entity.StartsAtUtc });
    }
}

internal sealed class RetentionRunRecordConfiguration
    : IEntityTypeConfiguration<RetentionRunRecord>
{
    public void Configure(EntityTypeBuilder<RetentionRunRecord> builder)
    {
        builder.ToTable("OperationsRetentionRuns");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.IdempotencyKey).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Policy).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.Target).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.Mode).HasMaxLength(20).IsRequired();
        builder.Property(entity => entity.Actor).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.CorrelationId).HasMaxLength(128).IsRequired();
        builder.HasIndex(entity => entity.IdempotencyKey).IsUnique();
    }
}

internal sealed class RetentionRunItemRecordConfiguration
    : IEntityTypeConfiguration<RetentionRunItemRecord>
{
    public void Configure(EntityTypeBuilder<RetentionRunItemRecord> builder)
    {
        builder.ToTable("OperationsRetentionRunItems");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Target).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.SubjectId).HasMaxLength(256).IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(20).IsRequired();
        builder.HasIndex(entity => new { entity.RunId, entity.Target, entity.SubjectId }).IsUnique();
        builder.HasIndex(entity => new { entity.Target, entity.SubjectId, entity.Status });
        builder.HasOne<RetentionRunRecord>()
            .WithMany()
            .HasForeignKey(entity => entity.RunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
