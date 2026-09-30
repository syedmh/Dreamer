using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HusayniaTabruk.Infrastructure.Migrations;

/// <summary>
/// Disposable-database EF representation of T8.  Production must use the owner scripts because
/// EF cannot keep a session lock through its provider-managed history write.
/// </summary>
public partial class T8CorrectivePostgresHardening : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.DisposableEfGuardSql,
            suppressTransaction: true);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.SchemaPreflightSql,
            suppressTransaction: true);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeUnderTopologyLockSql,
            suppressTransaction: true);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.CorrectiveObjectPreflightSql);
        migrationBuilder.Sql(
            """
            DO $tabruk$
            DECLARE
                target_schema text := current_setting('tabruk.target_schema', true);
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname = target_schema
                      AND relation.relname = 'ux_signups_waitlisted_order_per_help_need'
                ) THEN
                    EXECUTE format(
                        'CREATE UNIQUE INDEX %I ON %I.%I ("organization_id", "help_need_id", "waitlist_order") WHERE "status" = 2',
                        'ux_signups_waitlisted_order_per_help_need',
                        target_schema,
                        'signups');
                END IF;

            END
            $tabruk$;
            """,
            suppressTransaction: true);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.CorrectiveObjectPreflightSql);
        migrationBuilder.Sql(
            """
            DO $tabruk$
            DECLARE
                target_schema text := current_setting('tabruk.target_schema', true);
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_constraint AS constraint_definition
                    JOIN pg_catalog.pg_class AS relation
                      ON relation.oid = constraint_definition.conrelid
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname = target_schema
                      AND relation.relname = 'signups'
                      AND constraint_definition.conname = 'ck_signups_transition_chronology'
                ) THEN
                    EXECUTE format(
                        'ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK ("last_transition_at" IS NULL OR "last_transition_at" >= "submitted_at") NOT VALID',
                        target_schema,
                        'signups',
                        'ck_signups_transition_chronology');
                END IF;
            END
            $tabruk$;
            """,
            suppressTransaction: true);
        migrationBuilder.Sql(
            """
            DO $tabruk$
            DECLARE
                target_schema text := current_setting('tabruk.target_schema', true);
            BEGIN
                EXECUTE format(
                    'ALTER TABLE %I.%I VALIDATE CONSTRAINT %I',
                    target_schema,
                    'signups',
                    'ck_signups_transition_chronology');
            END
            $tabruk$;
            """);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.ValidatedCorrectiveObjectPreflightSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.LockedCanonicalReattestationSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.DisposableEfGuardSql,
            suppressTransaction: true);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.SchemaPreflightSql,
            suppressTransaction: true);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.EmptyApplicationTablesGuardSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.MigrationHistoryStructureAttestationSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.ManagedTableTopologyLockAndAttestationSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.ApplySafeDisposableDefaultPrivilegesSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.SchemaOwnerAndAclAttestationSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.ValidatedCorrectiveObjectPreflightSql);
        migrationBuilder.Sql(
            """
            DO $tabruk$
            DECLARE
                target_schema text := current_setting('tabruk.target_schema', true);
            BEGIN
                EXECUTE format(
                    'ALTER TABLE %I.%I DROP CONSTRAINT %I',
                    target_schema,
                    'signups',
                    'ck_signups_transition_chronology');
                EXECUTE format(
                    'DROP INDEX %I.%I',
                    target_schema,
                    'ux_signups_waitlisted_order_per_help_need');
            END
            $tabruk$;
            """);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeSql);
        migrationBuilder.Sql(
            PostgresLeastPrivilegeCatalog.RuntimePrivilegeAttestationSql);
    }
}
