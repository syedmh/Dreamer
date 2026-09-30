using Husaynia.Domain.Forms;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Forms;

internal sealed class FormDefinitionConfiguration
    : MutableEntityConfiguration<FormDefinition>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<FormDefinition> builder)
    {
        builder.ToTable(
            "FormDefinitions",
            table => table.HasCheckConstraint(
                "CK_FormDefinitions_PublishedWhenEnabled",
                "[IsEnabled] = 0 OR [PublishedVersionId] IS NOT NULL"));
        builder.HasKey(entity => entity.Id).HasName("PK_FormDefinitions");
        builder.Property(entity => entity.Key).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(entity => entity.Title).HasMaxLength(200).IsRequired();
        builder.HasIndex(entity => entity.Key)
            .HasDatabaseName("UX_FormDefinitions_Key")
            .IsUnique();
        builder.HasOne<FormDefinitionVersion>()
            .WithMany()
            .HasForeignKey(entity => entity.PublishedVersionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_FormDefinitions_PublishedVersion");
    }
}

internal sealed class FormDefinitionVersionConfiguration
    : IEntityTypeConfiguration<FormDefinitionVersion>
{
    public void Configure(EntityTypeBuilder<FormDefinitionVersion> builder)
    {
        builder.ToTable(
            "FormDefinitionVersions",
            table => table.HasCheckConstraint(
                "CK_FormDefinitionVersions_Version",
                "[Version] > 0"));
        builder.HasKey(entity => entity.Id).HasName("PK_FormDefinitionVersions");
        builder.Property(entity => entity.DestinationKey)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(entity => entity.TemplateKey)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(entity => entity.ConsentVersion).HasMaxLength(100);
        builder.Property(entity => entity.CreatedAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(entity => new { entity.DefinitionId, entity.Version })
            .HasDatabaseName("UX_FormDefinitionVersions_DefinitionId_Version")
            .IsUnique();
        builder.HasOne<FormDefinition>()
            .WithMany()
            .HasForeignKey(entity => entity.DefinitionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_FormDefinitionVersions_Definition");
    }
}

internal sealed class FormFieldConfiguration : IEntityTypeConfiguration<FormField>
{
    public void Configure(EntityTypeBuilder<FormField> builder)
    {
        builder.ToTable(
            "FormFields",
            table =>
            {
                table.HasCheckConstraint("CK_FormFields_Order", "[Order] BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_FormFields_Kind", "[Kind] BETWEEN 0 AND 6");
                table.HasCheckConstraint(
                    "CK_FormFields_PatternKind",
                    "[PatternKind] BETWEEN 0 AND 2");
                table.HasCheckConstraint(
                    "CK_FormFields_PrivacyClass",
                    "[PrivacyClass] BETWEEN 0 AND 2");
                table.HasCheckConstraint(
                    "CK_FormFields_LengthBounds",
                    "([MinimumLength] IS NULL OR [MinimumLength] >= 0) AND " +
                    "([MaximumLength] IS NULL OR [MaximumLength] BETWEEN 1 AND 4000) AND " +
                    "([MinimumLength] IS NULL OR [MaximumLength] IS NULL OR [MinimumLength] <= [MaximumLength])");
                table.HasCheckConstraint(
                    "CK_FormFields_ValueBounds",
                    "[MinimumValue] IS NULL OR [MaximumValue] IS NULL OR [MinimumValue] <= [MaximumValue]");
            });
        builder.HasKey(entity => entity.Id).HasName("PK_FormFields");
        builder.Property(entity => entity.Key).HasMaxLength(100).IsUnicode(false).IsRequired();
        builder.Property(entity => entity.Label).HasMaxLength(200).IsRequired();
        builder.Property(entity => entity.MinimumValue).HasPrecision(19, 4);
        builder.Property(entity => entity.MaximumValue).HasPrecision(19, 4);
        builder.Property(entity => entity.ChoicesJson).HasMaxLength(22_000).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(entity => new { entity.DefinitionVersionId, entity.Key })
            .HasDatabaseName("UX_FormFields_DefinitionVersionId_Key")
            .IsUnique();
        builder.HasIndex(entity => new { entity.DefinitionVersionId, entity.Order })
            .HasDatabaseName("UX_FormFields_DefinitionVersionId_Order")
            .IsUnique();
        builder.HasOne<FormDefinitionVersion>()
            .WithMany()
            .HasForeignKey(entity => entity.DefinitionVersionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_FormFields_DefinitionVersion");
    }
}

internal sealed class FormSubmissionConfiguration
    : MutableEntityConfiguration<FormSubmission>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<FormSubmission> builder)
    {
        builder.ToTable(
            "FormSubmissions",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_FormSubmissions_RetentionEligible",
                    "[RetentionEligibleAtUtc] > [AcceptedAtUtc]");
                table.HasCheckConstraint(
                    "CK_FormSubmissions_RetentionStatus",
                    "[RetentionStatus] BETWEEN 0 AND 2");
            });
        builder.HasKey(entity => entity.Id).HasName("PK_FormSubmissions");
        builder.Property(entity => entity.DuplicateFingerprint)
            .HasColumnType("binary(32)")
            .IsRequired();
        builder.Property(entity => entity.CanonicalPayloadHash)
            .HasColumnType("binary(32)")
            .IsRequired();
        builder.Property(entity => entity.DuplicateWindowStartUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.AcceptedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.RetentionEligibleAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.ConsentVersion).HasMaxLength(100);
        builder.Property(entity => entity.AnonymizedAtUtc).HasPrecision(7);
        builder.HasIndex(entity => new
            {
                entity.DefinitionVersionId,
                entity.DuplicateFingerprint,
                entity.DuplicateWindowStartUtc,
            })
            .HasDatabaseName(
                "UX_FormSubmissions_DefinitionVersionId_Fingerprint_Window")
            .IsUnique();
        builder.HasIndex(entity => new
            {
                entity.RetentionStatus,
                entity.RetentionEligibleAtUtc,
                entity.HasLegalHold,
            })
            .HasDatabaseName("IX_FormSubmissions_Retention");
        builder.HasIndex(entity => entity.DeliveryJobInstanceId)
            .HasDatabaseName("UX_FormSubmissions_DeliveryJobInstanceId")
            .IsUnique()
            .HasFilter("[DeliveryJobInstanceId] IS NOT NULL");
        builder.HasOne<FormDefinitionVersion>()
            .WithMany()
            .HasForeignKey(entity => entity.DefinitionVersionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_FormSubmissions_DefinitionVersion");
    }
}

internal sealed class FormSubmissionValueConfiguration
    : IEntityTypeConfiguration<FormSubmissionValue>
{
    public void Configure(EntityTypeBuilder<FormSubmissionValue> builder)
    {
        builder.ToTable(
            "FormSubmissionValues",
            table => table.HasCheckConstraint(
                "CK_FormSubmissionValues_PrivacyClass",
                "[PrivacyClass] BETWEEN 0 AND 2"));
        builder.HasKey(entity => entity.Id).HasName("PK_FormSubmissionValues");
        builder.Property(entity => entity.FieldKey)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(entity => entity.Value).HasMaxLength(4_000).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(entity => new { entity.SubmissionId, entity.FieldId })
            .HasDatabaseName("UX_FormSubmissionValues_SubmissionId_FieldId")
            .IsUnique();
        builder.HasOne<FormSubmission>()
            .WithMany()
            .HasForeignKey(entity => entity.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_FormSubmissionValues_Submission");
        builder.HasOne<FormField>()
            .WithMany()
            .HasForeignKey(entity => entity.FieldId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_FormSubmissionValues_Field");
    }
}

internal sealed class FormDeliveryAttemptConfiguration
    : MutableEntityConfiguration<FormDeliveryAttempt>
{
    protected override void ConfigureMutableEntity(
        EntityTypeBuilder<FormDeliveryAttempt> builder)
    {
        builder.ToTable(
            "FormDeliveryAttempts",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_FormDeliveryAttempts_AttemptNumber",
                    "[AttemptNumber] > 0");
                table.HasCheckConstraint(
                    "CK_FormDeliveryAttempts_Outcome",
                    "[Outcome] BETWEEN 0 AND 3");
                table.HasCheckConstraint(
                    "CK_FormDeliveryAttempts_Completion",
                    "([Outcome] = 0 AND [CompletedAtUtc] IS NULL) OR " +
                    "([Outcome] <> 0 AND [CompletedAtUtc] IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_FormDeliveryAttempts_Receipt",
                    "([Outcome] = 1 AND [ProviderReceiptHash] IS NOT NULL) OR " +
                    "([Outcome] <> 1 AND [ProviderReceiptHash] IS NULL)");
            });
        builder.HasKey(entity => entity.Id).HasName("PK_FormDeliveryAttempts");
        builder.Property(entity => entity.StartedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.CompletedAtUtc).HasPrecision(7);
        builder.Property(entity => entity.ErrorCode).HasMaxLength(100).IsUnicode(false);
        builder.Property(entity => entity.ProviderReceiptHash).HasColumnType("binary(32)");
        builder.HasIndex(entity => new
            {
                entity.SubmissionId,
                entity.JobInstanceId,
                entity.AttemptNumber,
            })
            .HasDatabaseName(
                "UX_FormDeliveryAttempts_SubmissionId_JobInstanceId_AttemptNumber")
            .IsUnique();
        builder.HasOne<FormSubmission>()
            .WithMany()
            .HasForeignKey(entity => entity.SubmissionId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_FormDeliveryAttempts_Submission");
    }
}

internal sealed class FormRateLimitConfiguration
    : IEntityTypeConfiguration<FormRateLimit>
{
    public void Configure(EntityTypeBuilder<FormRateLimit> builder)
    {
        builder.ToTable(
            "FormRateLimits",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_FormRateLimits_RequestCount",
                    "[RequestCount] > 0");
                table.HasCheckConstraint(
                    "CK_FormRateLimits_Window",
                    "[WindowEndsAtUtc] > [WindowStartedAtUtc]");
                table.HasCheckConstraint(
                    "CK_FormRateLimits_Retention",
                    "[RetainUntilUtc] >= [WindowEndsAtUtc]");
            });
        builder.HasKey(entity => entity.Id).HasName("PK_FormRateLimits");
        builder.Property(entity => entity.Id).ValueGeneratedOnAdd();
        builder.Property(entity => entity.FormKey)
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(entity => entity.ClientFingerprint)
            .HasColumnType("binary(32)")
            .IsRequired();
        builder.Property(entity => entity.WindowStartedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.WindowEndsAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.RetainUntilUtc).HasPrecision(7).IsRequired();
        builder.HasIndex(entity => new { entity.FormKey, entity.ClientFingerprint })
            .HasDatabaseName("UX_FormRateLimits_FormKey_ClientFingerprint")
            .IsUnique();
        builder.HasIndex(entity => entity.RetainUntilUtc)
            .HasDatabaseName("IX_FormRateLimits_RetainUntilUtc");
    }
}
