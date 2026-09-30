using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.Infrastructure.Persistence;

public sealed class TabrukDbContext
    : IdentityDbContext<TabrukIdentityUser, IdentityRole<Guid>, Guid>
{
    public TabrukDbContext(DbContextOptions<TabrukDbContext> options)
        : base(options)
    {
    }

    public DbSet<OrganizationEntity> Organizations => Set<OrganizationEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();
    public DbSet<InvitationEntity> Invitations => Set<InvitationEntity>();
    public DbSet<RoleAssignmentEntity> RoleAssignments => Set<RoleAssignmentEntity>();
    public DbSet<RoleChangeRequestEntity> RoleChangeRequests => Set<RoleChangeRequestEntity>();
    public DbSet<ServiceDateEntity> ServiceDates => Set<ServiceDateEntity>();
    public DbSet<HelpNeedEntity> HelpNeeds => Set<HelpNeedEntity>();
    public DbSet<SignupEntity> Signups => Set<SignupEntity>();
    public DbSet<SignupMemberParticipantEntity> SignupMemberParticipants => Set<SignupMemberParticipantEntity>();
    public DbSet<DateThreadEntity> DateThreads => Set<DateThreadEntity>();
    public DbSet<ThreadMessageEntity> ThreadMessages => Set<ThreadMessageEntity>();
    public DbSet<MessageReportEntity> MessageReports => Set<MessageReportEntity>();
    public DbSet<ThreadModerationEventEntity> ThreadModerationEvents => Set<ThreadModerationEventEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<DeviceRegistrationEntity> DeviceRegistrations => Set<DeviceRegistrationEntity>();
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<PrivilegedAccessEventEntity> PrivilegedAccessEvents => Set<PrivilegedAccessEventEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureIdentity(builder);
        ConfigureOrganizations(builder);
        ConfigureAccounts(builder);
        ConfigureDatesAndSignups(builder);
        ConfigureThreads(builder);
        ConfigureOperationalStorage(builder);
    }

    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TabrukIdentityUser>(entity =>
        {
            entity.ToTable("users");
            entity.Property(user => user.Id).HasColumnName("id");
            entity.Property(user => user.UserName).HasColumnName("user_name");
            entity.Property(user => user.NormalizedUserName).HasColumnName("normalized_user_name");
            entity.Property(user => user.Email).HasColumnName("email");
            entity.Property(user => user.NormalizedEmail).HasColumnName("normalized_email");
            entity.Property(user => user.EmailConfirmed).HasColumnName("email_confirmed");
            entity.Property(user => user.PasswordHash).HasColumnName("password_hash");
            entity.Property(user => user.SecurityStamp).HasColumnName("security_stamp");
            entity.Property(user => user.ConcurrencyStamp).HasColumnName("concurrency_stamp");
            entity.Property(user => user.PhoneNumber).HasColumnName("phone_number");
            entity.Property(user => user.PhoneNumberConfirmed).HasColumnName("phone_number_confirmed");
            entity.Property(user => user.TwoFactorEnabled).HasColumnName("two_factor_enabled");
            entity.Property(user => user.LockoutEnd).HasColumnName("lockout_end");
            entity.Property(user => user.LockoutEnabled).HasColumnName("lockout_enabled");
            entity.Property(user => user.AccessFailedCount).HasColumnName("access_failed_count");
        });

        modelBuilder.Entity<IdentityRole<Guid>>(entity =>
        {
            entity.ToTable("identity_roles");
            entity.Property(role => role.Id).HasColumnName("id");
            entity.Property(role => role.Name).HasColumnName("name");
            entity.Property(role => role.NormalizedName).HasColumnName("normalized_name");
            entity.Property(role => role.ConcurrencyStamp).HasColumnName("concurrency_stamp");
        });

        modelBuilder.Entity<IdentityUserRole<Guid>>().ToTable("identity_user_roles");
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("identity_user_claims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("identity_user_logins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("identity_user_tokens");
        modelBuilder.Entity<IdentityRoleClaim<Guid>>().ToTable("identity_role_claims");
    }

    private static void ConfigureOrganizations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrganizationEntity>(entity =>
        {
            entity.ToTable("organizations", table =>
            {
                table.HasCheckConstraint(
                    "ck_organizations_version_nonnegative",
                    "\"version\" >= 0");
                table.HasCheckConstraint(
                    "ck_organizations_bootstrap_state",
                    "(\"bootstrap_status\" = 0 AND \"bootstrap_sealed_at\" IS NULL) "
                    + "OR (\"bootstrap_status\" = 1 AND \"bootstrap_sealed_at\" IS NOT NULL)");
            });
            entity.HasKey(organization => organization.Id);
            entity.Property(organization => organization.Id).HasColumnName("id");
            entity.Property(organization => organization.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(organization => organization.TimeZone).HasColumnName("time_zone").HasMaxLength(100).IsRequired();
            entity.Property(organization => organization.DefaultCancellationLeadMinutes)
                .HasColumnName("default_cancellation_lead_minutes");
            entity.Property(organization => organization.Status).HasColumnName("status");
            entity.Property(organization => organization.BootstrapStatus).HasColumnName("bootstrap_status");
            entity.Property(organization => organization.BootstrapSealedAt)
                .HasColumnName("bootstrap_sealed_at")
                .HasColumnType("timestamp with time zone");
            entity.Property(organization => organization.Version)
                .HasColumnName("version")
                .IsConcurrencyToken();
        });
    }

    private static void ConfigureAccounts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MembershipEntity>(entity =>
        {
            entity.ToTable("memberships", table =>
            {
                table.HasCheckConstraint("ck_memberships_status", "\"status\" IN (0, 1, 2)");
            });
            entity.HasKey(membership => membership.Id);
            entity.HasAlternateKey(membership => new { membership.OrganizationId, membership.Id });
            entity.HasIndex(membership => new { membership.OrganizationId, membership.UserId }).IsUnique();
            entity.Property(membership => membership.Id).HasColumnName("id");
            entity.Property(membership => membership.OrganizationId).HasColumnName("organization_id");
            entity.Property(membership => membership.UserId).HasColumnName("user_id");
            entity.Property(membership => membership.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
            entity.Property(membership => membership.Status).HasColumnName("status");
            entity.Property(membership => membership.EligibleAsNamedParticipant)
                .HasColumnName("eligible_as_named_participant");
            entity.HasOne<OrganizationEntity>()
                .WithMany()
                .HasForeignKey(membership => membership.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TabrukIdentityUser>()
                .WithMany()
                .HasForeignKey(membership => membership.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InvitationEntity>(entity =>
        {
            entity.ToTable("invitations", table =>
            {
                table.HasCheckConstraint("ck_invitations_expiry", "\"expires_at\" > \"issued_at\"");
                table.HasCheckConstraint(
                    "ck_invitations_accept_revoke",
                    "\"accepted_at\" IS NULL OR \"revoked_at\" IS NULL");
            });
            entity.HasKey(invitation => invitation.Id);
            entity.HasIndex(invitation => new { invitation.OrganizationId, invitation.NormalizedEmail });
            entity.HasIndex(invitation => invitation.TokenHash).IsUnique();
            entity.Property(invitation => invitation.Id).HasColumnName("id");
            entity.Property(invitation => invitation.OrganizationId).HasColumnName("organization_id");
            entity.Property(invitation => invitation.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(256).IsRequired();
            entity.Property(invitation => invitation.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
            entity.Property(invitation => invitation.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.IssuedByMembershipId).HasColumnName("issued_by_membership_id");
            entity.Property(invitation => invitation.IssuedAt).HasColumnName("issued_at").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.AcceptedAt).HasColumnName("accepted_at").HasColumnType("timestamp with time zone");
            entity.Property(invitation => invitation.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamp with time zone");
            entity.HasOne<OrganizationEntity>().WithMany().HasForeignKey(invitation => invitation.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>()
                .WithMany()
                .HasForeignKey(invitation => new { invitation.OrganizationId, invitation.IssuedByMembershipId })
                .HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RoleAssignmentEntity>(entity =>
        {
            entity.ToTable("role_assignments", table =>
            {
                table.HasCheckConstraint("ck_role_assignments_role", "\"role\" IN (0, 1)");
                table.HasCheckConstraint(
                    "ck_role_assignments_revocation",
                    "(\"revoked_at\" IS NULL AND \"revoked_by_membership_id\" IS NULL) "
                    + "OR (\"revoked_at\" IS NOT NULL AND \"revoked_by_membership_id\" IS NOT NULL)");
            });
            entity.HasKey(assignment => assignment.Id);
            entity.HasIndex(assignment => new { assignment.OrganizationId, assignment.MembershipId, assignment.Role })
                .HasDatabaseName("ux_role_assignments_active")
                .IsUnique()
                .HasFilter("\"revoked_at\" IS NULL");
            entity.Property(assignment => assignment.Id).HasColumnName("id");
            entity.Property(assignment => assignment.OrganizationId).HasColumnName("organization_id");
            entity.Property(assignment => assignment.MembershipId).HasColumnName("membership_id");
            entity.Property(assignment => assignment.Role).HasColumnName("role");
            entity.Property(assignment => assignment.AssignedByMembershipId).HasColumnName("assigned_by_membership_id");
            entity.Property(assignment => assignment.AssignedAt).HasColumnName("assigned_at").HasColumnType("timestamp with time zone");
            entity.Property(assignment => assignment.RevokedByMembershipId).HasColumnName("revoked_by_membership_id");
            entity.Property(assignment => assignment.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamp with time zone");
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(assignment => new { assignment.OrganizationId, assignment.MembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(assignment => new { assignment.OrganizationId, assignment.AssignedByMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(assignment => new { assignment.OrganizationId, assignment.RevokedByMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RoleChangeRequestEntity>(entity =>
        {
            entity.ToTable("role_change_requests", table =>
            {
                table.HasCheckConstraint("ck_role_change_requests_action", "\"action\" IN (0, 1)");
                table.HasCheckConstraint("ck_role_change_requests_status", "\"status\" IN (0, 1)");
                table.HasCheckConstraint(
                    "ck_role_change_requests_timing",
                    "\"expires_at\" = \"proposed_at\" + INTERVAL '24 hours' "
                    + "AND (\"approved_at\" IS NULL OR (\"approved_at\" >= \"proposed_at\" AND \"approved_at\" < \"expires_at\"))");
                table.HasCheckConstraint(
                    "ck_role_change_requests_participants",
                    "\"proposer_membership_id\" <> \"target_membership_id\" "
                    + "AND (\"approver_membership_id\" IS NULL OR (\"approver_membership_id\" <> \"proposer_membership_id\" AND \"approver_membership_id\" <> \"target_membership_id\"))");
                table.HasCheckConstraint(
                    "ck_role_change_requests_approval_state",
                    "(\"status\" = 0 AND \"approver_membership_id\" IS NULL AND \"approved_at\" IS NULL) "
                    + "OR (\"status\" = 1 AND \"approver_membership_id\" IS NOT NULL AND \"approved_at\" IS NOT NULL)");
            });
            entity.HasKey(request => request.Id);
            entity.HasIndex(request => new { request.OrganizationId, request.Status, request.ExpiresAt });
            entity.Property(request => request.Id).HasColumnName("id");
            entity.Property(request => request.OrganizationId).HasColumnName("organization_id");
            entity.Property(request => request.TargetMembershipId).HasColumnName("target_membership_id");
            entity.Property(request => request.Action).HasColumnName("action");
            entity.Property(request => request.ProposerMembershipId).HasColumnName("proposer_membership_id");
            entity.Property(request => request.ApproverMembershipId).HasColumnName("approver_membership_id");
            entity.Property(request => request.Reason).HasColumnName("reason").HasMaxLength(2000).IsRequired();
            entity.Property(request => request.ProposedAt).HasColumnName("proposed_at").HasColumnType("timestamp with time zone");
            entity.Property(request => request.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
            entity.Property(request => request.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamp with time zone");
            entity.Property(request => request.Status).HasColumnName("status");
            entity.Property(request => request.BeforeState).HasColumnName("before_state");
            entity.Property(request => request.AfterState).HasColumnName("after_state");
            entity.HasOne<OrganizationEntity>().WithMany().HasForeignKey(request => request.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(request => new { request.OrganizationId, request.TargetMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(request => new { request.OrganizationId, request.ProposerMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(request => new { request.OrganizationId, request.ApproverMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureDatesAndSignups(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceDateEntity>(entity =>
        {
            entity.ToTable("service_dates", table =>
            {
                table.HasCheckConstraint(
                    "ck_service_dates_chronology",
                    "\"starts_at\" < \"ends_at\" AND \"cancellation_deadline_at\" <= \"starts_at\"");
                table.HasCheckConstraint("ck_service_dates_status", "\"status\" IN (0, 1, 2, 3, 4)");
                table.HasCheckConstraint("ck_service_dates_version", "\"version\" >= 0");
            });
            entity.HasKey(date => date.Id);
            entity.HasAlternateKey(date => new { date.OrganizationId, date.Id });
            entity.HasIndex(date => new { date.OrganizationId, date.Status, date.StartsAt });
            entity.Property(date => date.Id).HasColumnName("id");
            entity.Property(date => date.OrganizationId).HasColumnName("organization_id");
            entity.Property(date => date.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
            entity.Property(date => date.Instructions).HasColumnName("instructions").HasMaxLength(10000).IsRequired();
            entity.Property(date => date.StartsAt).HasColumnName("starts_at").HasColumnType("timestamp with time zone");
            entity.Property(date => date.EndsAt).HasColumnName("ends_at").HasColumnType("timestamp with time zone");
            entity.Property(date => date.CancellationDeadlineAt).HasColumnName("cancellation_deadline_at").HasColumnType("timestamp with time zone");
            entity.Property(date => date.ManagerMembershipId).HasColumnName("manager_membership_id");
            entity.Property(date => date.Status).HasColumnName("status");
            entity.Property(date => date.Version).HasColumnName("version").IsConcurrencyToken();
            entity.HasOne<OrganizationEntity>().WithMany().HasForeignKey(date => date.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(date => new { date.OrganizationId, date.ManagerMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HelpNeedEntity>(entity =>
        {
            entity.ToTable("help_needs", table =>
            {
                table.HasCheckConstraint("ck_help_needs_category", "\"category\" IN (0, 1, 2)");
                table.HasCheckConstraint("ck_help_needs_status", "\"status\" IN (0, 1)");
                table.HasCheckConstraint("ck_help_needs_capacity", "\"capacity\" IS NULL OR \"capacity\" > 0");
                table.HasCheckConstraint("ck_help_needs_version", "\"version\" >= 0 AND \"signup_version\" >= 0");
                table.HasCheckConstraint("ck_help_needs_waitlist_order_high_water", "\"waitlist_order_high_water\" >= 0");
            });
            entity.HasKey(need => need.Id);
            entity.HasAlternateKey(need => new { need.OrganizationId, need.Id });
            entity.HasAlternateKey(need => new { need.OrganizationId, need.ServiceDateId, need.Id });
            entity.HasIndex(need => new { need.ServiceDateId, need.Category }).IsUnique();
            entity.Property(need => need.Id).HasColumnName("id");
            entity.Property(need => need.OrganizationId).HasColumnName("organization_id");
            entity.Property(need => need.ServiceDateId).HasColumnName("service_date_id");
            entity.Property(need => need.Category).HasColumnName("category");
            entity.Property(need => need.Instructions).HasColumnName("instructions").HasMaxLength(10000).IsRequired();
            entity.Property(need => need.Capacity).HasColumnName("capacity");
            entity.Property(need => need.Status).HasColumnName("status");
            entity.Property(need => need.Version).HasColumnName("version");
            entity.Property(need => need.SignupVersion).HasColumnName("signup_version").IsConcurrencyToken();
            entity.Property(need => need.WaitlistOrderHighWater)
                .HasColumnName("waitlist_order_high_water")
                .HasDefaultValue(0L);
            entity.HasOne<ServiceDateEntity>().WithMany().HasForeignKey(need => new { need.OrganizationId, need.ServiceDateId }).HasPrincipalKey(date => new { date.OrganizationId, date.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SignupEntity>(entity =>
        {
            entity.ToTable("signups", table =>
            {
                table.HasCheckConstraint("ck_signups_kind", "\"kind\" IN (0, 1, 2)");
                table.HasCheckConstraint(
                    "ck_signups_label_unicode_scalars",
                    $"\"label\" IS NULL OR char_length(\"label\") <= {ApplicationLimits.MaximumSignupLabelUnicodeScalars}");
                table.HasCheckConstraint(
                    "ck_signups_label_utf8_bytes",
                    $"\"label\" IS NULL OR octet_length(convert_to(\"label\", 'UTF8')) <= {ApplicationLimits.MaximumSignupLabelUtf8Bytes}");
                table.HasCheckConstraint("ck_signups_status", "\"status\" IN (0, 1, 2, 3, 4, 5)");
                table.HasCheckConstraint("ck_signups_unnamed_participants", "\"unnamed_participant_count\" >= 0");
                table.HasCheckConstraint("ck_signups_version", "\"version\" >= 0");
                table.HasCheckConstraint(
                    "ck_signups_state_metadata",
                    "(\"status\" = 0 AND \"last_transition_at\" IS NULL AND \"waitlist_order\" IS NULL) "
                    + "OR (\"status\" = 2 AND \"last_transition_at\" IS NOT NULL AND \"waitlist_order\" > 0) "
                    + "OR (\"status\" IN (1, 3, 4, 5) AND \"last_transition_at\" IS NOT NULL AND \"waitlist_order\" IS NULL)");
                table.HasCheckConstraint(
                    "ck_signups_transition_chronology",
                    "\"last_transition_at\" IS NULL OR \"last_transition_at\" >= \"submitted_at\"");
            });
            entity.HasKey(signup => signup.Id);
            entity.HasAlternateKey(signup => new { signup.OrganizationId, signup.Id });
            entity.HasIndex(signup => new { signup.OrganizationId, signup.HelpNeedId, signup.PrimaryMembershipId })
                .HasDatabaseName("ux_signups_active_primary_per_help_need")
                .IsUnique()
                .HasFilter("\"status\" IN (0, 1, 2)");
            entity.HasIndex(signup => new { signup.OrganizationId, signup.HelpNeedId, signup.WaitlistOrder })
                .HasDatabaseName("ux_signups_waitlisted_order_per_help_need")
                .IsUnique()
                .HasFilter("\"status\" = 2");
            entity.HasIndex(signup => new { signup.HelpNeedId, signup.Status, signup.WaitlistOrder });
            entity.Property(signup => signup.Id).HasColumnName("id");
            entity.Property(signup => signup.OrganizationId).HasColumnName("organization_id");
            entity.Property(signup => signup.ServiceDateId).HasColumnName("service_date_id");
            entity.Property(signup => signup.HelpNeedId).HasColumnName("help_need_id");
            entity.Property(signup => signup.PrimaryMembershipId).HasColumnName("primary_membership_id");
            entity.Property(signup => signup.Kind).HasColumnName("kind");
            entity.Property(signup => signup.Label)
                .HasColumnName("label")
                .HasMaxLength(ApplicationLimits.MaximumSignupLabelUnicodeScalars);
            entity.Property(signup => signup.UnnamedParticipantCount).HasColumnName("unnamed_participant_count");
            entity.Property(signup => signup.Status).HasColumnName("status");
            entity.Property(signup => signup.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamp with time zone");
            entity.Property(signup => signup.LastTransitionAt).HasColumnName("last_transition_at").HasColumnType("timestamp with time zone");
            entity.Property(signup => signup.WaitlistOrder).HasColumnName("waitlist_order");
            entity.Property(signup => signup.Version).HasColumnName("version");
            entity.HasOne<ServiceDateEntity>().WithMany().HasForeignKey(signup => new { signup.OrganizationId, signup.ServiceDateId }).HasPrincipalKey(date => new { date.OrganizationId, date.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<HelpNeedEntity>().WithMany().HasForeignKey(signup => new { signup.OrganizationId, signup.ServiceDateId, signup.HelpNeedId }).HasPrincipalKey(need => new { need.OrganizationId, need.ServiceDateId, need.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(signup => new { signup.OrganizationId, signup.PrimaryMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SignupMemberParticipantEntity>(entity =>
        {
            entity.ToTable("signup_member_participants");
            entity.HasKey(participant => new { participant.SignupId, participant.MembershipId });
            entity.Property(participant => participant.SignupId).HasColumnName("signup_id");
            entity.Property(participant => participant.OrganizationId).HasColumnName("organization_id");
            entity.Property(participant => participant.MembershipId).HasColumnName("membership_id");
            entity.HasOne<SignupEntity>().WithMany().HasForeignKey(participant => new { participant.OrganizationId, participant.SignupId }).HasPrincipalKey(signup => new { signup.OrganizationId, signup.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(participant => new { participant.OrganizationId, participant.MembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureThreads(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DateThreadEntity>(entity =>
        {
            entity.ToTable("date_threads", table =>
            {
                table.HasCheckConstraint(
                    "ck_date_threads_lock_state",
                    "(\"status\" = 0 AND \"locked_at\" IS NULL) OR (\"status\" = 1 AND \"locked_at\" IS NOT NULL)");
                table.HasCheckConstraint("ck_date_threads_version_nonnegative", "\"version\" >= 0");
            });
            entity.HasKey(thread => thread.Id);
            entity.HasAlternateKey(thread => new { thread.OrganizationId, thread.Id });
            entity.HasIndex(thread => thread.ServiceDateId).IsUnique();
            entity.Property(thread => thread.Id).HasColumnName("id");
            entity.Property(thread => thread.OrganizationId).HasColumnName("organization_id");
            entity.Property(thread => thread.ServiceDateId).HasColumnName("service_date_id");
            entity.Property(thread => thread.Status).HasColumnName("status");
            entity.Property(thread => thread.LockedAt).HasColumnName("locked_at").HasColumnType("timestamp with time zone");
            entity.Property(thread => thread.Version).HasColumnName("version").IsConcurrencyToken();
            entity.HasOne<ServiceDateEntity>().WithMany().HasForeignKey(thread => new { thread.OrganizationId, thread.ServiceDateId }).HasPrincipalKey(date => new { date.OrganizationId, date.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ThreadMessageEntity>(entity =>
        {
            entity.ToTable("thread_messages", table =>
            {
                table.HasCheckConstraint(
                    "ck_thread_messages_visibility",
                    "(\"visibility\" = 0 AND \"hidden_at\" IS NULL) OR (\"visibility\" = 1 AND \"hidden_at\" IS NOT NULL)");
            });
            entity.HasKey(message => message.Id);
            entity.HasAlternateKey(message => new { message.OrganizationId, message.Id });
            entity.HasIndex(message => new { message.ThreadId, message.ClientMessageId }).IsUnique();
            entity.Property(message => message.Id).HasColumnName("id");
            entity.Property(message => message.OrganizationId).HasColumnName("organization_id");
            entity.Property(message => message.ThreadId).HasColumnName("thread_id");
            entity.Property(message => message.AuthorMembershipId).HasColumnName("author_membership_id");
            entity.Property(message => message.ClientMessageId).HasColumnName("client_message_id");
            entity.Property(message => message.Body).HasColumnName("body").HasMaxLength(4000).IsRequired();
            entity.Property(message => message.Visibility).HasColumnName("visibility");
            entity.Property(message => message.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(message => message.HiddenAt).HasColumnName("hidden_at").HasColumnType("timestamp with time zone");
            entity.HasOne<DateThreadEntity>().WithMany().HasForeignKey(message => new { message.OrganizationId, message.ThreadId }).HasPrincipalKey(thread => new { thread.OrganizationId, thread.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(message => new { message.OrganizationId, message.AuthorMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MessageReportEntity>(entity =>
        {
            entity.ToTable(
                "message_reports",
                table =>
                {
                    table.HasCheckConstraint("ck_message_reports_reason", "\"reason\" IN (0, 1, 2, 3)");
                    table.HasCheckConstraint("ck_message_reports_state", "\"state\" = 0");
                });
            entity.HasKey(report => new { report.MessageId, report.ReporterMembershipId });
            entity.HasIndex(report => new { report.OrganizationId, report.ReportedAt });
            entity.Property(report => report.OrganizationId).HasColumnName("organization_id");
            entity.Property(report => report.MessageId).HasColumnName("message_id");
            entity.Property(report => report.ReporterMembershipId).HasColumnName("reporter_membership_id");
            entity.Property(report => report.Reason).HasColumnName("reason");
            entity.Property(report => report.Comment).HasColumnName("comment").HasMaxLength(2000);
            entity.Property(report => report.State).HasColumnName("state").HasDefaultValue((short)0);
            entity.Property(report => report.ReportedAt).HasColumnName("reported_at").HasColumnType("timestamp with time zone");
            entity.HasOne<ThreadMessageEntity>().WithMany().HasForeignKey(report => new { report.OrganizationId, report.MessageId }).HasPrincipalKey(message => new { message.OrganizationId, message.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(report => new { report.OrganizationId, report.ReporterMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ThreadModerationEventEntity>(entity =>
        {
            entity.ToTable("thread_moderation_events", table => table.HasCheckConstraint("ck_thread_moderation_events_action", "\"action\" IN ('hide', 'lock')"));
            entity.HasKey(moderation => moderation.Id);
            entity.HasIndex(moderation => new { moderation.ThreadId, moderation.OccurredAt });
            entity.Property(moderation => moderation.Id).HasColumnName("id");
            entity.Property(moderation => moderation.OrganizationId).HasColumnName("organization_id");
            entity.Property(moderation => moderation.ThreadId).HasColumnName("thread_id");
            entity.Property(moderation => moderation.MessageId).HasColumnName("message_id");
            entity.Property(moderation => moderation.ActorMembershipId).HasColumnName("actor_membership_id");
            entity.Property(moderation => moderation.Action).HasColumnName("action").HasMaxLength(10).IsRequired();
            entity.Property(moderation => moderation.Reason).HasColumnName("reason").HasMaxLength(2000).IsRequired();
            entity.Property(moderation => moderation.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
            entity.HasOne<DateThreadEntity>().WithMany().HasForeignKey(moderation => new { moderation.OrganizationId, moderation.ThreadId }).HasPrincipalKey(thread => new { thread.OrganizationId, thread.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ThreadMessageEntity>().WithMany().HasForeignKey(moderation => new { moderation.OrganizationId, moderation.MessageId }).HasPrincipalKey(message => new { message.OrganizationId, message.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(moderation => new { moderation.OrganizationId, moderation.ActorMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureOperationalStorage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationEntity>(entity =>
        {
            entity.ToTable("notifications", table =>
            {
                table.HasCheckConstraint("ck_notifications_type", "\"type\" IN (0, 1, 2, 3)");
                table.HasCheckConstraint("ck_notifications_resource_type", "\"resource_type\" IN (0, 1, 2, 3)");
                table.HasCheckConstraint("ck_notifications_read_chronology", "\"read_at\" IS NULL OR \"read_at\" >= \"created_at\"");
            });
            entity.HasKey(notification => notification.Id);
            entity.HasIndex(notification => new { notification.OrganizationId, notification.RecipientMembershipId, notification.ReadAt, notification.CreatedAt });
            entity.Property(notification => notification.Id).HasColumnName("id");
            entity.Property(notification => notification.OrganizationId).HasColumnName("organization_id");
            entity.Property(notification => notification.RecipientMembershipId).HasColumnName("recipient_membership_id");
            entity.Property(notification => notification.Type).HasColumnName("type");
            entity.Property(notification => notification.ResourceType).HasColumnName("resource_type");
            entity.Property(notification => notification.ResourceId).HasColumnName("resource_id");
            entity.Property(notification => notification.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
            entity.Property(notification => notification.Body).HasColumnName("body").HasMaxLength(2000).IsRequired();
            entity.Property(notification => notification.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(notification => notification.ReadAt).HasColumnName("read_at").HasColumnType("timestamp with time zone");
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(notification => new { notification.OrganizationId, notification.RecipientMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeviceRegistrationEntity>(entity =>
        {
            entity.ToTable("device_registrations");
            entity.HasKey(device => device.Id);
            entity.HasIndex(device => new { device.OrganizationId, device.MembershipId, device.InstallationId }).IsUnique();
            entity.Property(device => device.Id).HasColumnName("id");
            entity.Property(device => device.OrganizationId).HasColumnName("organization_id");
            entity.Property(device => device.MembershipId).HasColumnName("membership_id");
            entity.Property(device => device.InstallationId).HasColumnName("installation_id");
            entity.Property(device => device.ProviderToken).HasColumnName("provider_token").HasMaxLength(4096).IsRequired();
            entity.Property(device => device.Platform).HasColumnName("platform").HasMaxLength(50).IsRequired();
            entity.Property(device => device.Enabled).HasColumnName("enabled");
            entity.Property(device => device.LastSeenAt).HasColumnName("last_seen_at").HasColumnType("timestamp with time zone");
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(device => new { device.OrganizationId, device.MembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OutboxMessageEntity>(entity =>
        {
            entity.ToTable("outbox_messages", table => table.HasCheckConstraint("ck_outbox_messages_attempts", "\"attempts\" >= 0"));
            entity.HasKey(message => message.Id);
            entity.HasIndex(message => new { message.ProcessedAt, message.DeadLetteredAt, message.NextAttemptAt });
            entity.Property(message => message.Id).HasColumnName("id");
            entity.Property(message => message.OrganizationId).HasColumnName("organization_id");
            entity.Property(message => message.Type).HasColumnName("type").HasMaxLength(200).IsRequired();
            entity.Property(message => message.Payload).HasColumnName("payload").IsRequired();
            entity.Property(message => message.Attempts).HasColumnName("attempts");
            entity.Property(message => message.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
            entity.Property(message => message.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamp with time zone");
            entity.Property(message => message.DeadLetteredAt).HasColumnName("dead_lettered_at").HasColumnType("timestamp with time zone");
            entity.Property(message => message.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
            entity.HasOne<OrganizationEntity>().WithMany().HasForeignKey(message => message.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IdempotencyRecordEntity>(entity =>
        {
            entity.ToTable("idempotency_records", table =>
            {
                table.HasCheckConstraint("ck_idempotency_records_status", "\"status\" IN (0, 1, 2)");
                table.HasCheckConstraint("ck_idempotency_records_expiry", "\"expires_at\" > \"created_at\"");
                table.HasCheckConstraint(
                    "ck_idempotency_records_result",
                    "(\"status\" = 1 AND \"result_reference\" IS NOT NULL) OR (\"status\" IN (0, 2) AND \"result_reference\" IS NULL)");
            });
            entity.HasKey(record => new { record.OrganizationId, record.MembershipId, record.Key });
            entity.HasIndex(record => new { record.OrganizationId, record.ExpiresAt });
            entity.Property(record => record.OrganizationId).HasColumnName("organization_id");
            entity.Property(record => record.MembershipId).HasColumnName("membership_id");
            entity.Property(record => record.Key).HasColumnName("key");
            entity.Property(record => record.Operation).HasColumnName("operation").HasMaxLength(200).IsRequired();
            entity.Property(record => record.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(record => record.Status).HasColumnName("status");
            entity.Property(record => record.ResultReference).HasColumnName("result_reference").HasMaxLength(500);
            entity.Property(record => record.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(record => record.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(record => new { record.OrganizationId, record.MembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(audit => audit.Id);
            entity.HasIndex(audit => new { audit.OrganizationId, audit.OccurredAt });
            entity.Property(audit => audit.Id).HasColumnName("id");
            entity.Property(audit => audit.OrganizationId).HasColumnName("organization_id");
            entity.Property(audit => audit.ActorMembershipId).HasColumnName("actor_membership_id");
            entity.Property(audit => audit.Action).HasColumnName("action").HasMaxLength(200).IsRequired();
            entity.Property(audit => audit.ResourceType).HasColumnName("resource_type").HasMaxLength(100).IsRequired();
            entity.Property(audit => audit.ResourceId).HasColumnName("resource_id").HasMaxLength(200).IsRequired();
            entity.Property(audit => audit.Reason).HasColumnName("reason").HasMaxLength(2000).IsRequired();
            entity.Property(audit => audit.Purpose).HasColumnName("purpose").HasMaxLength(200).IsRequired();
            entity.Property(audit => audit.CorrelationId).HasColumnName("correlation_id").HasMaxLength(200).IsRequired();
            entity.Property(audit => audit.BeforeState).HasColumnName("before_state");
            entity.Property(audit => audit.AfterState).HasColumnName("after_state");
            entity.Property(audit => audit.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(audit => new { audit.OrganizationId, audit.ActorMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PrivilegedAccessEventEntity>(entity =>
        {
            entity.ToTable("privileged_access_events", table => table.HasCheckConstraint("ck_privileged_access_events_purpose", "\"purpose\" IN (0, 1, 2)"));
            entity.HasKey(access => access.Id);
            entity.HasIndex(access => new { access.OrganizationId, access.ActorMembershipId, access.OccurredAt });
            entity.Property(access => access.Id).HasColumnName("id");
            entity.Property(access => access.OrganizationId).HasColumnName("organization_id");
            entity.Property(access => access.ActorMembershipId).HasColumnName("actor_membership_id");
            entity.Property(access => access.ResourceType).HasColumnName("resource_type").HasMaxLength(100).IsRequired();
            entity.Property(access => access.ResourceId).HasColumnName("resource_id").HasMaxLength(200).IsRequired();
            entity.Property(access => access.Reason).HasColumnName("reason").HasMaxLength(2000).IsRequired();
            entity.Property(access => access.Purpose).HasColumnName("purpose");
            entity.Property(access => access.CaseId).HasColumnName("case_id").HasMaxLength(200).IsRequired();
            entity.Property(access => access.PageCursor).HasColumnName("page_cursor").HasMaxLength(1000);
            entity.Property(access => access.PageHash).HasColumnName("page_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(access => access.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
            entity.HasOne<MembershipEntity>().WithMany().HasForeignKey(access => new { access.OrganizationId, access.ActorMembershipId }).HasPrincipalKey(membership => new { membership.OrganizationId, membership.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
