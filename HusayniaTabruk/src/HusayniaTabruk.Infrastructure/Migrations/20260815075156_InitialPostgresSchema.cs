using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable IDE0161
#pragma warning disable CA1861

namespace HusayniaTabruk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgresSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "identity_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    default_cancellation_lead_minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    bootstrap_status = table.Column<short>(type: "smallint", nullable: false),
                    bootstrap_sealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_bootstrap_state", "(\"bootstrap_status\" = 0 AND \"bootstrap_sealed_at\" IS NULL) OR (\"bootstrap_status\" = 1 AND \"bootstrap_sealed_at\" IS NOT NULL)");
                    table.CheckConstraint("ck_organizations_version_nonnegative", "\"version\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    security_stamp = table.Column<string>(type: "text", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "identity_role_claims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_role_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_identity_role_claims_identity_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts", "\"attempts\" >= 0");
                    table.ForeignKey(
                        name: "FK_outbox_messages_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_claims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_identity_user_claims_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_logins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_logins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_identity_user_logins_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_roles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_roles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_identity_user_roles_identity_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "identity_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_identity_user_roles_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "identity_user_tokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_user_tokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_identity_user_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    eligible_as_named_participant = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => x.id);
                    table.UniqueConstraint("AK_memberships_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_memberships_status", "\"status\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    before_state = table.Column<string>(type: "text", nullable: true),
                    after_state = table.Column<string>(type: "text", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_events_memberships_organization_id_actor_membership_id",
                        columns: x => new { x.organization_id, x.actor_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_token = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    platform = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_registrations", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_registrations_memberships_organization_id_membership~",
                        columns: x => new { x.organization_id, x.membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    result_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => new { x.organization_id, x.membership_id, x.key });
                    table.CheckConstraint("ck_idempotency_records_expiry", "\"expires_at\" > \"created_at\"");
                    table.CheckConstraint("ck_idempotency_records_result", "(\"status\" = 1 AND \"result_reference\" IS NOT NULL) OR (\"status\" IN (0, 2) AND \"result_reference\" IS NULL)");
                    table.CheckConstraint("ck_idempotency_records_status", "\"status\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_idempotency_records_memberships_organization_id_membership_~",
                        columns: x => new { x.organization_id, x.membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.id);
                    table.CheckConstraint("ck_invitations_accept_revoke", "\"accepted_at\" IS NULL OR \"revoked_at\" IS NULL");
                    table.CheckConstraint("ck_invitations_expiry", "\"expires_at\" > \"issued_at\"");
                    table.ForeignKey(
                        name: "FK_invitations_memberships_organization_id_issued_by_membershi~",
                        columns: x => new { x.organization_id, x.issued_by_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invitations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<short>(type: "smallint", nullable: false),
                    resource_type = table.Column<short>(type: "smallint", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_read_chronology", "\"read_at\" IS NULL OR \"read_at\" >= \"created_at\"");
                    table.CheckConstraint("ck_notifications_resource_type", "\"resource_type\" IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_notifications_type", "\"type\" IN (0, 1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_notifications_memberships_organization_id_recipient_members~",
                        columns: x => new { x.organization_id, x.recipient_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "privileged_access_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    purpose = table.Column<short>(type: "smallint", nullable: false),
                    case_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    page_cursor = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    page_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privileged_access_events", x => x.id);
                    table.CheckConstraint("ck_privileged_access_events_purpose", "\"purpose\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_privileged_access_events_memberships_organization_id_actor_~",
                        columns: x => new { x.organization_id, x.actor_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<short>(type: "smallint", nullable: false),
                    assigned_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_assignments", x => x.id);
                    table.CheckConstraint("ck_role_assignments_revocation", "(\"revoked_at\" IS NULL AND \"revoked_by_membership_id\" IS NULL) OR (\"revoked_at\" IS NOT NULL AND \"revoked_by_membership_id\" IS NOT NULL)");
                    table.CheckConstraint("ck_role_assignments_role", "\"role\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_role_assignments_memberships_organization_id_assigned_by_me~",
                        columns: x => new { x.organization_id, x.assigned_by_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_assignments_memberships_organization_id_membership_id",
                        columns: x => new { x.organization_id, x.membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_assignments_memberships_organization_id_revoked_by_mem~",
                        columns: x => new { x.organization_id, x.revoked_by_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_change_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<short>(type: "smallint", nullable: false),
                    proposer_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approver_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    proposed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    before_state = table.Column<string>(type: "text", nullable: true),
                    after_state = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_change_requests", x => x.id);
                    table.CheckConstraint("ck_role_change_requests_action", "\"action\" IN (0, 1)");
                    table.CheckConstraint("ck_role_change_requests_approval_state", "(\"status\" = 0 AND \"approver_membership_id\" IS NULL AND \"approved_at\" IS NULL) OR (\"status\" = 1 AND \"approver_membership_id\" IS NOT NULL AND \"approved_at\" IS NOT NULL)");
                    table.CheckConstraint("ck_role_change_requests_participants", "\"proposer_membership_id\" <> \"target_membership_id\" AND (\"approver_membership_id\" IS NULL OR (\"approver_membership_id\" <> \"proposer_membership_id\" AND \"approver_membership_id\" <> \"target_membership_id\"))");
                    table.CheckConstraint("ck_role_change_requests_status", "\"status\" IN (0, 1)");
                    table.CheckConstraint("ck_role_change_requests_timing", "\"expires_at\" = \"proposed_at\" + INTERVAL '24 hours' AND (\"approved_at\" IS NULL OR (\"approved_at\" >= \"proposed_at\" AND \"approved_at\" < \"expires_at\"))");
                    table.ForeignKey(
                        name: "FK_role_change_requests_memberships_organization_id_approver_m~",
                        columns: x => new { x.organization_id, x.approver_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_change_requests_memberships_organization_id_proposer_m~",
                        columns: x => new { x.organization_id, x.proposer_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_change_requests_memberships_organization_id_target_mem~",
                        columns: x => new { x.organization_id, x.target_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_change_requests_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_dates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    instructions = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancellation_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    manager_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_dates", x => x.id);
                    table.UniqueConstraint("AK_service_dates_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_service_dates_chronology", "\"starts_at\" < \"ends_at\" AND \"cancellation_deadline_at\" <= \"starts_at\"");
                    table.CheckConstraint("ck_service_dates_status", "\"status\" IN (0, 1, 2, 3, 4)");
                    table.CheckConstraint("ck_service_dates_version", "\"version\" >= 0");
                    table.ForeignKey(
                        name: "FK_service_dates_memberships_organization_id_manager_membershi~",
                        columns: x => new { x.organization_id, x.manager_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_dates_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "date_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_date_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_date_threads", x => x.id);
                    table.UniqueConstraint("AK_date_threads_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_date_threads_lock_state", "(\"status\" = 0 AND \"locked_at\" IS NULL) OR (\"status\" = 1 AND \"locked_at\" IS NOT NULL)");
                    table.CheckConstraint("ck_date_threads_version_nonnegative", "\"version\" >= 0");
                    table.ForeignKey(
                        name: "FK_date_threads_service_dates_organization_id_service_date_id",
                        columns: x => new { x.organization_id, x.service_date_id },
                        principalTable: "service_dates",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "help_needs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_date_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<short>(type: "smallint", nullable: false),
                    instructions = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    signup_version = table.Column<long>(type: "bigint", nullable: false),
                    waitlist_order_high_water = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_help_needs", x => x.id);
                    table.UniqueConstraint("AK_help_needs_organization_id_id", x => new { x.organization_id, x.id });
                    table.UniqueConstraint("AK_help_needs_organization_id_service_date_id_id", x => new { x.organization_id, x.service_date_id, x.id });
                    table.CheckConstraint("ck_help_needs_capacity", "\"capacity\" IS NULL OR \"capacity\" > 0");
                    table.CheckConstraint("ck_help_needs_category", "\"category\" IN (0, 1, 2)");
                    table.CheckConstraint("ck_help_needs_status", "\"status\" IN (0, 1)");
                    table.CheckConstraint("ck_help_needs_version", "\"version\" >= 0 AND \"signup_version\" >= 0");
                    table.CheckConstraint("ck_help_needs_waitlist_order_high_water", "\"waitlist_order_high_water\" >= 0");
                    table.ForeignKey(
                        name: "FK_help_needs_service_dates_organization_id_service_date_id",
                        columns: x => new { x.organization_id, x.service_date_id },
                        principalTable: "service_dates",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "thread_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    visibility = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    hidden_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_messages", x => x.id);
                    table.UniqueConstraint("AK_thread_messages_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_thread_messages_visibility", "(\"visibility\" = 0 AND \"hidden_at\" IS NULL) OR (\"visibility\" = 1 AND \"hidden_at\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_thread_messages_date_threads_organization_id_thread_id",
                        columns: x => new { x.organization_id, x.thread_id },
                        principalTable: "date_threads",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_thread_messages_memberships_organization_id_author_membersh~",
                        columns: x => new { x.organization_id, x.author_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "signups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_date_id = table.Column<Guid>(type: "uuid", nullable: false),
                    help_need_id = table.Column<Guid>(type: "uuid", nullable: false),
                    primary_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    unnamed_participant_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_transition_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waitlist_order = table.Column<long>(type: "bigint", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signups", x => x.id);
                    table.UniqueConstraint("AK_signups_organization_id_id", x => new { x.organization_id, x.id });
                    table.CheckConstraint("ck_signups_kind", "\"kind\" IN (0, 1, 2)");
                    table.CheckConstraint("ck_signups_label_unicode_scalars", "\"label\" IS NULL OR char_length(\"label\") <= 80");
                    table.CheckConstraint("ck_signups_label_utf8_bytes", "\"label\" IS NULL OR octet_length(convert_to(\"label\", 'UTF8')) <= 320");
                    table.CheckConstraint("ck_signups_state_metadata", "(\"status\" = 0 AND \"last_transition_at\" IS NULL AND \"waitlist_order\" IS NULL) OR (\"status\" = 2 AND \"last_transition_at\" IS NOT NULL AND \"waitlist_order\" > 0) OR (\"status\" IN (1, 3, 4, 5) AND \"last_transition_at\" IS NOT NULL AND \"waitlist_order\" IS NULL)");
                    table.CheckConstraint("ck_signups_status", "\"status\" IN (0, 1, 2, 3, 4, 5)");
                    table.CheckConstraint("ck_signups_unnamed_participants", "\"unnamed_participant_count\" >= 0");
                    table.CheckConstraint("ck_signups_version", "\"version\" >= 0");
                    table.ForeignKey(
                        name: "FK_signups_help_needs_organization_id_service_date_id_help_nee~",
                        columns: x => new { x.organization_id, x.service_date_id, x.help_need_id },
                        principalTable: "help_needs",
                        principalColumns: new[] { "organization_id", "service_date_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_signups_memberships_organization_id_primary_membership_id",
                        columns: x => new { x.organization_id, x.primary_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_signups_service_dates_organization_id_service_date_id",
                        columns: x => new { x.organization_id, x.service_date_id },
                        principalTable: "service_dates",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_reports",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<short>(type: "smallint", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_reports", x => new { x.message_id, x.reporter_membership_id });
                    table.CheckConstraint("ck_message_reports_reason", "\"reason\" IN (0, 1, 2, 3)");
                    table.CheckConstraint("ck_message_reports_state", "\"state\" = 0");
                    table.ForeignKey(
                        name: "FK_message_reports_memberships_organization_id_reporter_member~",
                        columns: x => new { x.organization_id, x.reporter_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_reports_thread_messages_organization_id_message_id",
                        columns: x => new { x.organization_id, x.message_id },
                        principalTable: "thread_messages",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "thread_moderation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_moderation_events", x => x.id);
                    table.CheckConstraint("ck_thread_moderation_events_action", "\"action\" IN ('hide', 'lock')");
                    table.ForeignKey(
                        name: "FK_thread_moderation_events_date_threads_organization_id_threa~",
                        columns: x => new { x.organization_id, x.thread_id },
                        principalTable: "date_threads",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_thread_moderation_events_memberships_organization_id_actor_~",
                        columns: x => new { x.organization_id, x.actor_membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_thread_moderation_events_thread_messages_organization_id_me~",
                        columns: x => new { x.organization_id, x.message_id },
                        principalTable: "thread_messages",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "signup_member_participants",
                columns: table => new
                {
                    signup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signup_member_participants", x => new { x.signup_id, x.membership_id });
                    table.ForeignKey(
                        name: "FK_signup_member_participants_memberships_organization_id_memb~",
                        columns: x => new { x.organization_id, x.membership_id },
                        principalTable: "memberships",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_signup_member_participants_signups_organization_id_signup_id",
                        columns: x => new { x.organization_id, x.signup_id },
                        principalTable: "signups",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_organization_id_actor_membership_id",
                table: "audit_events",
                columns: new[] { "organization_id", "actor_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_organization_id_occurred_at",
                table: "audit_events",
                columns: new[] { "organization_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_date_threads_organization_id_service_date_id",
                table: "date_threads",
                columns: new[] { "organization_id", "service_date_id" });

            migrationBuilder.CreateIndex(
                name: "IX_date_threads_service_date_id",
                table: "date_threads",
                column: "service_date_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_registrations_organization_id_membership_id_installa~",
                table: "device_registrations",
                columns: new[] { "organization_id", "membership_id", "installation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_help_needs_service_date_id_category",
                table: "help_needs",
                columns: new[] { "service_date_id", "category" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_organization_id_expires_at",
                table: "idempotency_records",
                columns: new[] { "organization_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_role_claims_RoleId",
                table: "identity_role_claims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "identity_roles",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_claims_UserId",
                table: "identity_user_claims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_logins_UserId",
                table: "identity_user_logins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_identity_user_roles_RoleId",
                table: "identity_user_roles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_organization_id_issued_by_membership_id",
                table: "invitations",
                columns: new[] { "organization_id", "issued_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_organization_id_normalized_email",
                table: "invitations",
                columns: new[] { "organization_id", "normalized_email" });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_token_hash",
                table: "invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memberships_organization_id_user_id",
                table: "memberships",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_memberships_user_id",
                table: "memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_message_reports_organization_id_message_id",
                table: "message_reports",
                columns: new[] { "organization_id", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_message_reports_organization_id_reported_at",
                table: "message_reports",
                columns: new[] { "organization_id", "reported_at" });

            migrationBuilder.CreateIndex(
                name: "IX_message_reports_organization_id_reporter_membership_id",
                table: "message_reports",
                columns: new[] { "organization_id", "reporter_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_organization_id_recipient_membership_id_read_~",
                table: "notifications",
                columns: new[] { "organization_id", "recipient_membership_id", "read_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_organization_id",
                table: "outbox_messages",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_processed_at_dead_lettered_at_next_attempt_~",
                table: "outbox_messages",
                columns: new[] { "processed_at", "dead_lettered_at", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "IX_privileged_access_events_organization_id_actor_membership_i~",
                table: "privileged_access_events",
                columns: new[] { "organization_id", "actor_membership_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_organization_id_assigned_by_membership_id",
                table: "role_assignments",
                columns: new[] { "organization_id", "assigned_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_role_assignments_organization_id_revoked_by_membership_id",
                table: "role_assignments",
                columns: new[] { "organization_id", "revoked_by_membership_id" });

            migrationBuilder.CreateIndex(
                name: "ux_role_assignments_active",
                table: "role_assignments",
                columns: new[] { "organization_id", "membership_id", "role" },
                unique: true,
                filter: "\"revoked_at\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_role_change_requests_organization_id_approver_membership_id",
                table: "role_change_requests",
                columns: new[] { "organization_id", "approver_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_role_change_requests_organization_id_proposer_membership_id",
                table: "role_change_requests",
                columns: new[] { "organization_id", "proposer_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_role_change_requests_organization_id_status_expires_at",
                table: "role_change_requests",
                columns: new[] { "organization_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_role_change_requests_organization_id_target_membership_id",
                table: "role_change_requests",
                columns: new[] { "organization_id", "target_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_service_dates_organization_id_manager_membership_id",
                table: "service_dates",
                columns: new[] { "organization_id", "manager_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_service_dates_organization_id_status_starts_at",
                table: "service_dates",
                columns: new[] { "organization_id", "status", "starts_at" });

            migrationBuilder.CreateIndex(
                name: "IX_signup_member_participants_organization_id_membership_id",
                table: "signup_member_participants",
                columns: new[] { "organization_id", "membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_signup_member_participants_organization_id_signup_id",
                table: "signup_member_participants",
                columns: new[] { "organization_id", "signup_id" });

            migrationBuilder.CreateIndex(
                name: "IX_signups_help_need_id_status_waitlist_order",
                table: "signups",
                columns: new[] { "help_need_id", "status", "waitlist_order" });

            migrationBuilder.CreateIndex(
                name: "IX_signups_organization_id_primary_membership_id",
                table: "signups",
                columns: new[] { "organization_id", "primary_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_signups_organization_id_service_date_id_help_need_id",
                table: "signups",
                columns: new[] { "organization_id", "service_date_id", "help_need_id" });

            migrationBuilder.CreateIndex(
                name: "ux_signups_active_primary_per_help_need",
                table: "signups",
                columns: new[] { "organization_id", "help_need_id", "primary_membership_id" },
                unique: true,
                filter: "\"status\" IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_thread_messages_organization_id_author_membership_id",
                table: "thread_messages",
                columns: new[] { "organization_id", "author_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_thread_messages_organization_id_thread_id",
                table: "thread_messages",
                columns: new[] { "organization_id", "thread_id" });

            migrationBuilder.CreateIndex(
                name: "IX_thread_messages_thread_id_client_message_id",
                table: "thread_messages",
                columns: new[] { "thread_id", "client_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_thread_moderation_events_organization_id_actor_membership_id",
                table: "thread_moderation_events",
                columns: new[] { "organization_id", "actor_membership_id" });

            migrationBuilder.CreateIndex(
                name: "IX_thread_moderation_events_organization_id_message_id",
                table: "thread_moderation_events",
                columns: new[] { "organization_id", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_thread_moderation_events_organization_id_thread_id",
                table: "thread_moderation_events",
                columns: new[] { "organization_id", "thread_id" });

            migrationBuilder.CreateIndex(
                name: "IX_thread_moderation_events_thread_id_occurred_at",
                table: "thread_moderation_events",
                columns: new[] { "thread_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "users",
                column: "normalized_user_name",
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION tabruk_prevent_signup_service_date_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.service_date_id IS DISTINCT FROM OLD.service_date_id THEN
                        RAISE EXCEPTION 'signups.service_date_id is immutable';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tr_signups_prevent_service_date_change
                BEFORE UPDATE ON signups
                FOR EACH ROW
                EXECUTE FUNCTION tabruk_prevent_signup_service_date_change();
                """);

            // The DBA pre-provisions tabruk_app; this migration only grants schema-level access.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    target_schema name := current_schema();
                BEGIN
                    EXECUTE format('GRANT USAGE ON SCHEMA %I TO tabruk_app', target_schema);
                    EXECUTE format(
                        'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO tabruk_app',
                        target_schema);
                    EXECUTE format(
                        'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I TO tabruk_app',
                        target_schema);
                    EXECUTE format(
                        'REVOKE ALL PRIVILEGES ON TABLE %I.audit_events, %I.privileged_access_events FROM PUBLIC',
                        target_schema,
                        target_schema);
                    EXECUTE format(
                        'REVOKE ALL PRIVILEGES ON TABLE %I.audit_events, %I.privileged_access_events FROM tabruk_app',
                        target_schema,
                        target_schema);
                    EXECUTE format(
                        'GRANT INSERT ON TABLE %I.audit_events, %I.privileged_access_events TO tabruk_app',
                        target_schema,
                        target_schema);
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    target_schema name := current_schema();
                BEGIN
                    EXECUTE format(
                        'REVOKE USAGE, SELECT ON ALL SEQUENCES IN SCHEMA %I FROM tabruk_app',
                        target_schema);
                    EXECUTE format(
                        'REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA %I FROM tabruk_app',
                        target_schema);
                    EXECUTE format('REVOKE USAGE ON SCHEMA %I FROM tabruk_app', target_schema);
                END
                $$;
                """);

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "device_registrations");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "identity_role_claims");

            migrationBuilder.DropTable(
                name: "identity_user_claims");

            migrationBuilder.DropTable(
                name: "identity_user_logins");

            migrationBuilder.DropTable(
                name: "identity_user_roles");

            migrationBuilder.DropTable(
                name: "identity_user_tokens");

            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropTable(
                name: "message_reports");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "privileged_access_events");

            migrationBuilder.DropTable(
                name: "role_assignments");

            migrationBuilder.DropTable(
                name: "role_change_requests");

            migrationBuilder.DropTable(
                name: "signup_member_participants");

            migrationBuilder.DropTable(
                name: "thread_moderation_events");

            migrationBuilder.DropTable(
                name: "identity_roles");

            migrationBuilder.DropTable(
                name: "signups");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS tabruk_prevent_signup_service_date_change();");

            migrationBuilder.DropTable(
                name: "thread_messages");

            migrationBuilder.DropTable(
                name: "help_needs");

            migrationBuilder.DropTable(
                name: "date_threads");

            migrationBuilder.DropTable(
                name: "service_dates");

            migrationBuilder.DropTable(
                name: "memberships");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
