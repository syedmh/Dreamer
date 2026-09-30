using Husaynia.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Identity;

internal sealed class HusayniaIdentityUserConfiguration
    : IEntityTypeConfiguration<HusayniaIdentityUser>
{
    public void Configure(EntityTypeBuilder<HusayniaIdentityUser> builder)
    {
        builder.ToTable("IdentityUsers");
        builder.HasKey(user => user.Id);
        builder.HasIndex(user => user.NormalizedUserName)
            .HasDatabaseName("IX_IdentityUsers_NormalizedUserName")
            .IsUnique();
        builder.HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("IX_IdentityUsers_NormalizedEmail")
            .IsUnique();
        builder.Property(user => user.ConcurrencyStamp).IsConcurrencyToken();
        builder.Property(user => user.UserName).HasMaxLength(256);
        builder.Property(user => user.NormalizedUserName).HasMaxLength(256);
        builder.Property(user => user.Email).HasMaxLength(256);
        builder.Property(user => user.NormalizedEmail).HasMaxLength(256);
        builder.Property(user => user.PhoneNumber).HasMaxLength(50);
        builder.Property(user => user.InvitationTokenHash).HasMaxLength(128);
        builder.Property(user => user.InvitationIssuedAtUtc).HasPrecision(7);
        builder.Property(user => user.InvitationExpiresAtUtc).HasPrecision(7);
        builder.Property(user => user.DisabledAtUtc).HasPrecision(7);
        builder.HasMany<IdentityUserClaim<Guid>>()
            .WithOne()
            .HasForeignKey(claim => claim.UserId)
            .IsRequired();
        builder.HasMany<IdentityUserLogin<Guid>>()
            .WithOne()
            .HasForeignKey(login => login.UserId)
            .IsRequired();
        builder.HasMany<IdentityUserToken<Guid>>()
            .WithOne()
            .HasForeignKey(token => token.UserId)
            .IsRequired();
        builder.HasMany<IdentityUserRole<Guid>>()
            .WithOne()
            .HasForeignKey(role => role.UserId)
            .IsRequired();
    }
}

internal sealed class HusayniaIdentityRoleConfiguration
    : IEntityTypeConfiguration<HusayniaIdentityRole>
{
    public void Configure(EntityTypeBuilder<HusayniaIdentityRole> builder)
    {
        builder.ToTable("IdentityRoles");
        builder.HasKey(role => role.Id);
        builder.HasIndex(role => role.NormalizedName)
            .HasDatabaseName("IX_IdentityRoles_NormalizedName")
            .IsUnique();
        builder.Property(role => role.ConcurrencyStamp).IsConcurrencyToken();
        builder.Property(role => role.Name).HasMaxLength(256);
        builder.Property(role => role.NormalizedName).HasMaxLength(256);
        builder.HasMany<IdentityUserRole<Guid>>()
            .WithOne()
            .HasForeignKey(role => role.RoleId)
            .IsRequired();
        builder.HasMany<IdentityRoleClaim<Guid>>()
            .WithOne()
            .HasForeignKey(claim => claim.RoleId)
            .IsRequired();
    }
}

internal sealed class HusayniaIdentityUserRoleConfiguration
    : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
        builder.ToTable("IdentityUserRoles");
        builder.HasKey(role => new { role.UserId, role.RoleId });
    }
}

internal sealed class HusayniaIdentityUserClaimConfiguration
    : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
    {
        builder.ToTable("IdentityUserClaims");
        builder.HasKey(claim => claim.Id);
    }
}

internal sealed class HusayniaIdentityUserLoginConfiguration
    : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
    {
        builder.ToTable("IdentityUserLogins");
        builder.HasKey(login => new { login.LoginProvider, login.ProviderKey });
        builder.Property(login => login.LoginProvider).HasMaxLength(128);
        builder.Property(login => login.ProviderKey).HasMaxLength(128);
    }
}

internal sealed class HusayniaIdentityUserTokenConfiguration
    : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.ToTable("IdentityUserTokens");
        builder.HasKey(token => new { token.UserId, token.LoginProvider, token.Name });
        builder.Property(token => token.LoginProvider).HasMaxLength(128);
        builder.Property(token => token.Name).HasMaxLength(128);
    }
}

internal sealed class HusayniaIdentityRoleClaimConfiguration
    : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
    {
        builder.ToTable("IdentityRoleClaims");
        builder.HasKey(claim => claim.Id);
    }
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("IdentityAuditEvents");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.ActorId).HasMaxLength(256);
        builder.Property(entity => entity.RolesJson).HasMaxLength(2_000).IsRequired();
        builder.Property(entity => entity.Action).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.TargetType).HasMaxLength(100).IsRequired();
        builder.Property(entity => entity.TargetId).HasMaxLength(256);
        builder.Property(entity => entity.Outcome).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.OccurredAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.DetailJson).HasMaxLength(4_000).IsRequired();
        builder.HasIndex(entity => entity.OccurredAtUtc);
        builder.HasIndex(entity => new { entity.TargetType, entity.TargetId });
        builder.HasIndex(entity => entity.CorrelationId);
    }
}

internal sealed class IdentityBootstrapStateConfiguration
    : IEntityTypeConfiguration<IdentityBootstrapState>
{
    public void Configure(EntityTypeBuilder<IdentityBootstrapState> builder)
    {
        builder.ToTable("IdentityBootstrapState");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).ValueGeneratedNever();
        builder.Property(entity => entity.EnvironmentName).HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.AdministratorUserId).IsRequired();
        builder.Property(entity => entity.SealedAtUtc).HasPrecision(7).IsRequired();
    }
}

internal sealed class IdentityAnonymousRateLimitConfiguration
    : IEntityTypeConfiguration<IdentityAnonymousRateLimit>
{
    public void Configure(EntityTypeBuilder<IdentityAnonymousRateLimit> builder)
    {
        builder.ToTable(
            "IdentityAnonymousRateLimits",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_IdentityAnonymousRateLimits_RequestCount",
                    "[RequestCount] > 0");
                table.HasCheckConstraint(
                    "CK_IdentityAnonymousRateLimits_Window",
                    "[WindowEndsAtUtc] > [WindowStartedAtUtc]");
                table.HasCheckConstraint(
                    "CK_IdentityAnonymousRateLimits_Retention",
                    "[RetainUntilUtc] >= [WindowEndsAtUtc]");
            });
        builder.HasKey(entity => entity.Id)
            .HasName("PK_IdentityAnonymousRateLimits")
            .IsClustered();
        builder.Property(entity => entity.Id).ValueGeneratedOnAdd();
        builder.Property(entity => entity.EndpointFamily).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.ClientFingerprint)
            .HasColumnType("binary(32)")
            .IsRequired();
        builder.Property(entity => entity.WindowStartedAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.WindowEndsAtUtc).HasPrecision(7).IsRequired();
        builder.Property(entity => entity.RequestCount).IsRequired();
        builder.Property(entity => entity.RetainUntilUtc).HasPrecision(7).IsRequired();
        builder.HasAlternateKey(entity => new
        {
            entity.EndpointFamily,
            entity.ClientFingerprint,
        })
            .HasName("UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint");
        builder.HasIndex(entity => entity.RetainUntilUtc)
            .HasDatabaseName("IX_IdentityAnonymousRateLimits_RetainUntilUtc");
    }
}

public sealed class IdentityBootstrapState
{
    public const int PermanentSealId = 1;

    private IdentityBootstrapState()
    {
    }

    public IdentityBootstrapState(
        string environmentName,
        Guid administratorUserId,
        DateTimeOffset sealedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(environmentName) || environmentName.Trim().Length > 128)
        {
            throw new ArgumentException("A bounded environment name is required.", nameof(environmentName));
        }

        Id = PermanentSealId;
        EnvironmentName = environmentName.Trim();
        AdministratorUserId = administratorUserId;
        SealedAtUtc = sealedAtUtc.ToUniversalTime();
    }

    public int Id { get; private set; }

    public string EnvironmentName { get; private set; } = string.Empty;

    public Guid AdministratorUserId { get; private set; }

    public DateTimeOffset SealedAtUtc { get; private set; }
}

public sealed class IdentityAnonymousRateLimit
{
    private IdentityAnonymousRateLimit()
    {
    }

    public long Id { get; private set; }

    public string EndpointFamily { get; private set; } = string.Empty;

    public byte[] ClientFingerprint { get; private set; } = [];

    public DateTimeOffset WindowStartedAtUtc { get; private set; }

    public DateTimeOffset WindowEndsAtUtc { get; private set; }

    public int RequestCount { get; private set; }

    public DateTimeOffset RetainUntilUtc { get; private set; }
}
