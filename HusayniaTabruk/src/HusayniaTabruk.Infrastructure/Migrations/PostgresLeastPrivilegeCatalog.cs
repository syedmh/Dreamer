using System.Collections.ObjectModel;
using System.Linq;

namespace HusayniaTabruk.Infrastructure.Migrations;

/// <summary>
/// The single catalog of fixed PostgreSQL objects and attestation SQL used by the disposable EF
/// path and the checked-in owner scripts.  SQL never trusts ambient application-object
/// resolution: the target namespace OID is captured from <c>tabruk.target_schema</c>, and every
/// relation lookup thereafter is bound to that OID.
/// </summary>
public static class PostgresLeastPrivilegeCatalog
{
    public const string ApplicationRole = "tabruk_app";
    public const string MigrationHistoryTable = "__EFMigrationsHistory";
    public const string InitialMigrationId = "20260815075156_InitialPostgresSchema";
    public const string CorrectiveMigrationId = "20260815102612_T8CorrectivePostgresHardening";
    public const string WaitlistIndex = "ux_signups_waitlisted_order_per_help_need";
    public const string ChronologyConstraint = "ck_signups_transition_chronology";
    public const string TargetSchemaSetting = "tabruk.target_schema";
    public const string DisposableEfSetting = "tabruk.disposable_ef";

    private static readonly string[] CrudTableNames =
    [
        "users",
        "identity_roles",
        "identity_user_claims",
        "identity_role_claims",
        "identity_user_logins",
        "identity_user_roles",
        "identity_user_tokens",
        "organizations",
        "memberships",
        "invitations",
        "role_assignments",
        "role_change_requests",
        "service_dates",
        "help_needs",
        "signups",
        "signup_member_participants",
        "date_threads",
        "thread_messages",
        "message_reports",
        "thread_moderation_events",
        "notifications",
        "device_registrations",
        "outbox_messages",
        "idempotency_records",
    ];

    private static readonly string[] InsertOnlyTableNames =
    [
        "audit_events",
        "privileged_access_events",
    ];

    private static readonly string[] DeniedTableNames =
    [
        MigrationHistoryTable,
    ];

    private static readonly string[] UsageSelectSequenceNames =
    [
        "identity_role_claims_Id_seq",
        "identity_user_claims_Id_seq",
    ];

    private static readonly string[] PostgreSql18TablePrivilegeNames =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "DELETE",
        "TRUNCATE",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    private static readonly string[] PostgreSql18ColumnPrivilegeNames =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "REFERENCES",
    ];

    private static readonly string[] CrudTablePrivilegeNames =
    [
        "SELECT",
        "INSERT",
        "UPDATE",
        "DELETE",
    ];

    private static readonly string[] CrudDeniedTablePrivilegeNames =
    [
        "TRUNCATE",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    private static readonly string[] CrudDeniedColumnPrivilegeNames =
    [
        "REFERENCES",
    ];

    private static readonly string[] InsertOnlyDeniedTablePrivilegeNames =
    [
        "SELECT",
        "UPDATE",
        "DELETE",
        "TRUNCATE",
        "REFERENCES",
        "TRIGGER",
        "MAINTAIN",
    ];

    private static readonly string[] InsertOnlyDeniedColumnPrivilegeNames =
    [
        "SELECT",
        "UPDATE",
        "REFERENCES",
    ];

    private static readonly string[] ClassifiedTableNames =
    [
        .. CrudTableNames,
        .. InsertOnlyTableNames,
    ];

    private static readonly string[] ManagedTableNames =
    [
        .. ClassifiedTableNames,
        .. DeniedTableNames,
    ];

    private static readonly string[] AllFixedRelationNames =
    [
        .. ManagedTableNames,
        .. UsageSelectSequenceNames,
        WaitlistIndex,
    ];

    public static ReadOnlyCollection<string> CrudTables { get; } =
        Array.AsReadOnly(CrudTableNames);

    public static ReadOnlyCollection<string> InsertOnlyTables { get; } =
        Array.AsReadOnly(InsertOnlyTableNames);

    public static ReadOnlyCollection<string> DeniedTables { get; } =
        Array.AsReadOnly(DeniedTableNames);

    public static ReadOnlyCollection<string> UsageSelectSequences { get; } =
        Array.AsReadOnly(UsageSelectSequenceNames);

    public static ReadOnlyCollection<string> PostgreSql18TablePrivileges { get; } =
        Array.AsReadOnly(PostgreSql18TablePrivilegeNames);

    public static ReadOnlyCollection<string> PostgreSql18ColumnPrivileges { get; } =
        Array.AsReadOnly(PostgreSql18ColumnPrivilegeNames);

    public static ReadOnlyCollection<string> ClassifiedTables { get; } =
        Array.AsReadOnly(ClassifiedTableNames);

    public static ReadOnlyCollection<string> ManagedTables { get; } =
        Array.AsReadOnly(ManagedTableNames);

    public static string DisposableEfGuardSql { get; } =
        """
        DO $tabruk$
        BEGIN
            IF current_setting('tabruk.disposable_ef', true) IS DISTINCT FROM 'on' THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'T8_ATTESTATION_FAILED:disposable; EF migrations require tabruk.disposable_ef=on.';
            END IF;
        END
        $tabruk$;
        """;

    public static string SchemaPreflightSql { get; } =
        """
        DO $tabruk$
        DECLARE
            expected_schema text := nullif(current_setting('tabruk.target_schema', true), '');
            raw_search_path text := current_setting('search_path');
            search_path_entries text[];
            normalized_search_path text;
            target_namespace oid;
        BEGIN
            search_path_entries := regexp_split_to_array(raw_search_path, '\s*,\s*');
            normalized_search_path := CASE
                WHEN COALESCE(array_length(search_path_entries, 1), 0) = 1
                THEN btrim(search_path_entries[1])
                ELSE NULL
            END;

            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname = expected_schema;

            IF expected_schema IS NULL
                OR expected_schema !~ '^[a-z_][a-z0-9_$]{0,62}$'
                OR expected_schema IN ('pg_catalog', 'information_schema')
                OR expected_schema LIKE 'pg\_%' ESCAPE '\'
                OR COALESCE(array_length(search_path_entries, 1), 0) <> 1
                OR normalized_search_path IS DISTINCT FROM expected_schema
                OR target_namespace IS NULL
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = format(
                        'T8 schema contract mismatch: expected_schema=%s; search_path=%s; T8_ATTESTATION_FAILED:schema.',
                        COALESCE(expected_schema, '<missing>'),
                        COALESCE(raw_search_path, '<missing>'));
            END IF;

        END
        $tabruk$;
        """;

    public static string SessionAdvisoryLockSql { get; } =
        """
        SELECT pg_catalog.pg_advisory_lock(
            pg_catalog.hashtextextended(
                pg_catalog.current_database()
                || ':'
                || (
                    SELECT namespace_definition.oid::text
                    FROM pg_catalog.pg_namespace AS namespace_definition
                    WHERE namespace_definition.nspname =
                        pg_catalog.current_setting('tabruk.target_schema', true)
                )
                || ':T8',
                0));
        """;

    public static string RuntimeHardeningLockSql { get; } =
        """
        SELECT pg_catalog.pg_advisory_xact_lock(
            pg_catalog.hashtextextended(
                pg_catalog.current_database()
                || ':runtime-hardening:'
                || pg_catalog.current_setting('tabruk.target_schema', true),
                0));
        """;

    public static string EmptyApplicationTablesGuardSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
            managed_table text;
            has_rows boolean;
        BEGIN
            FOREACH managed_table IN ARRAY {{SqlTextArray(ClassifiedTableNames)}}
            LOOP
                EXECUTE format(
                    'SELECT EXISTS (SELECT 1 FROM %I.%I LIMIT 1)',
                    target_schema,
                    managed_table)
                INTO has_rows;
                IF has_rows THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'T8_ATTESTATION_FAILED:object; EF destructive Down requires every application table to be empty.';
                END IF;
            END LOOP;
        END
        $tabruk$;
        """;

    public static string InvalidInitialInventorySql { get; } =
        """
        DO $tabruk$
        BEGIN
            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'T8 pre-history attestation failed: partial-initial-inventory. T8_ATTESTATION_FAILED:history.';
        END
        $tabruk$;
        """;

    public static string MigrationHistoryWriteLockSql(string validatedSchema) =>
        $"LOCK TABLE {QuoteIdentifier(validatedSchema)}.{QuoteIdentifier(MigrationHistoryTable)} IN ACCESS EXCLUSIVE MODE;";

    public static string ManagedTableTopologyLockSqlForSchema(string validatedSchema) =>
        $"LOCK TABLE {JoinSchemaQualifiedIdentifiers(validatedSchema, ManagedTableNames)} IN ACCESS EXCLUSIVE MODE;";

    public static string ManagedTableTopologyLockSql { get; } =
        """
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
        BEGIN
            EXECUTE format(
                'LOCK TABLE %s IN ACCESS EXCLUSIVE MODE',
                (
                    SELECT string_agg(format('%I.%I', target_schema, managed_table.table_name), ', ')
                    FROM unnest(ARRAY[
                        'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
                        'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
                        'organizations', 'memberships', 'invitations', 'role_assignments',
                        'role_change_requests', 'service_dates', 'help_needs', 'signups',
                        'signup_member_participants', 'date_threads', 'thread_messages',
                        'message_reports', 'thread_moderation_events', 'notifications',
                        'device_registrations', 'outbox_messages', 'idempotency_records',
                        'audit_events', 'privileged_access_events', '__EFMigrationsHistory'])
                        AS managed_table(table_name)
                ));
        END
        $tabruk$;
        """;

    public static string ManagedTableTopologyLockAndAttestationSql =>
        string.Join(
            Environment.NewLine,
            ManagedTableTopologyLockSql,
            ManagedTableTopologyAttestationSql);

    public static string ManagedTableTopologyAttestationSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
            target_namespace oid;
        BEGIN
            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname = target_schema;

            IF target_namespace IS NULL
                OR EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    WHERE relation.relnamespace = pg_catalog.pg_my_temp_schema()
                      AND relation.relname = ANY({{SqlTextArray(AllFixedRelationNames)}})
                )
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: temp-shadow. T8_ATTESTATION_FAILED:temp.';
            END IF;

            IF (
                SELECT count(*)
                FROM pg_catalog.pg_class AS relation
                WHERE relation.relnamespace = target_namespace
                  AND relation.relname = ANY({{SqlTextArray(ManagedTableNames)}})
                  AND relation.relkind = 'r'
                  AND NOT relation.relispartition
                  AND relation.relpartbound IS NULL
            ) <> {{ManagedTableNames.Length}}
                OR EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    WHERE relation.relnamespace = target_namespace
                      AND relation.relname = ANY({{SqlTextArray(ManagedTableNames)}})
                      AND (
                          relation.relkind <> 'r'
                          OR relation.relispartition
                          OR relation.relpartbound IS NOT NULL
                          OR EXISTS (
                              SELECT 1
                              FROM pg_catalog.pg_inherits AS inheritance
                              WHERE inheritance.inhparent = relation.oid
                                 OR inheritance.inhrelid = relation.oid)
                      )
                )
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: managed-table-topology-mismatch. T8_ATTESTATION_FAILED:object.';
            END IF;
        END
        $tabruk$;
        """;

    public static string SchemaOwnerAndAclAttestationSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
            target_namespace oid;
            owner_oid oid;
            application_oid oid;
        BEGIN
            SELECT namespace_definition.oid, namespace_definition.nspowner
            INTO target_namespace, owner_oid
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname = target_schema;

            SELECT role_definition.oid
            INTO application_oid
            FROM pg_catalog.pg_roles AS role_definition
            WHERE role_definition.rolname = '{{ApplicationRole}}';

            IF target_namespace IS NULL
                OR owner_oid <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)
                OR application_oid IS NULL
                OR EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_roles AS role_definition
                    WHERE role_definition.oid = application_oid
                      AND (
                          role_definition.rolcanlogin
                          OR role_definition.rolsuper
                          OR role_definition.rolcreatedb
                          OR role_definition.rolcreaterole
                          OR role_definition.rolreplication
                          OR role_definition.rolbypassrls
                          OR role_definition.rolname = current_user
                      )
                )
                OR pg_catalog.has_schema_privilege('public', target_namespace, 'USAGE')
                OR pg_catalog.has_schema_privilege('public', target_namespace, 'CREATE')
                OR NOT pg_catalog.has_schema_privilege('{{ApplicationRole}}', target_namespace, 'USAGE')
                OR pg_catalog.has_schema_privilege('{{ApplicationRole}}', target_namespace, 'CREATE')
                OR NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_namespace AS namespace_definition
                    CROSS JOIN LATERAL pg_catalog.aclexplode(
                        COALESCE(
                            namespace_definition.nspacl,
                            pg_catalog.acldefault('n', namespace_definition.nspowner)))
                        AS privilege
                    WHERE namespace_definition.oid = target_namespace
                      AND privilege.grantee = application_oid
                      AND privilege.privilege_type = 'USAGE'
                      AND NOT privilege.is_grantable
                )
                OR EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_namespace AS namespace_definition
                    CROSS JOIN LATERAL pg_catalog.aclexplode(
                        COALESCE(
                            namespace_definition.nspacl,
                            pg_catalog.acldefault('n', namespace_definition.nspowner)))
                        AS privilege
                    WHERE namespace_definition.oid = target_namespace
                      AND privilege.is_grantable
                      AND privilege.grantee <> 0
                      AND (
                          privilege.grantee = application_oid
                          OR pg_catalog.pg_has_role('{{ApplicationRole}}', privilege.grantee, 'MEMBER')
                      )
                )
                OR EXISTS (
                    SELECT 1
                    FROM (
                        VALUES
                            (0::oid, 'r'::"char"),
                            (0::oid, 'S'::"char"),
                            (0::oid, 'f'::"char"),
                            (0::oid, 'T'::"char"),
                            (target_namespace, 'r'::"char"),
                            (target_namespace, 'S'::"char"),
                            (target_namespace, 'f'::"char"),
                            (target_namespace, 'T'::"char")
                    ) AS default_scope(namespace_oid, object_type)
                    LEFT JOIN pg_catalog.pg_default_acl AS default_acl
                      ON default_acl.defaclrole = owner_oid
                     AND default_acl.defaclnamespace = default_scope.namespace_oid
                     AND default_acl.defaclobjtype = default_scope.object_type
                    CROSS JOIN LATERAL pg_catalog.aclexplode(
                        COALESCE(
                            default_acl.defaclacl,
                            pg_catalog.acldefault(default_scope.object_type, owner_oid)))
                        AS privilege
                    WHERE privilege.grantee = 0
                       OR privilege.grantee = application_oid
                       OR pg_catalog.pg_has_role(
                           '{{ApplicationRole}}',
                           privilege.grantee,
                           'MEMBER')
                )
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: owner-acl. T8_ATTESTATION_FAILED:acl.';
            END IF;
        END
        $tabruk$;
        """;

    public static string ApplySafeDefaultPrivilegesSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
            inherited_role record;
            object_kind text;
        BEGIN
            FOREACH object_kind IN ARRAY ARRAY['TABLES', 'SEQUENCES', 'FUNCTIONS', 'TYPES']
            LOOP
                EXECUTE format(
                    'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT ALL PRIVILEGES ON %s TO %I',
                    current_user,
                    target_schema,
                    object_kind,
                    current_user);
                EXECUTE format(
                    'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I REVOKE ALL PRIVILEGES ON %s FROM PUBLIC, %I',
                    current_user,
                    target_schema,
                    object_kind,
                    '{{ApplicationRole}}');

                FOR inherited_role IN
                    SELECT role_definition.rolname
                    FROM pg_catalog.pg_roles AS role_definition
                    WHERE role_definition.rolname NOT IN (current_user, '{{ApplicationRole}}')
                      AND pg_catalog.pg_has_role(
                          '{{ApplicationRole}}',
                          role_definition.oid,
                          'MEMBER')
                LOOP
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I REVOKE ALL PRIVILEGES ON %s FROM %I',
                        current_user,
                        target_schema,
                        object_kind,
                        inherited_role.rolname);
                END LOOP;
            END LOOP;
        END
        $tabruk$;
        """;

    public static string ApplySafeDisposableDefaultPrivilegesSql =>
        string.Join(
            Environment.NewLine,
            $$"""
            DO $tabruk$
            DECLARE
                inherited_role record;
                object_kind text;
            BEGIN
                FOREACH object_kind IN ARRAY ARRAY['TABLES', 'SEQUENCES', 'FUNCTIONS', 'TYPES']
                LOOP
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT ALL PRIVILEGES ON %s TO %I',
                        current_user,
                        object_kind,
                        current_user);
                    EXECUTE format(
                        'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE ALL PRIVILEGES ON %s FROM PUBLIC, %I',
                        current_user,
                        object_kind,
                        '{{ApplicationRole}}');

                    FOR inherited_role IN
                        SELECT role_definition.rolname
                        FROM pg_catalog.pg_roles AS role_definition
                        WHERE role_definition.rolname NOT IN (current_user, '{{ApplicationRole}}')
                          AND pg_catalog.pg_has_role(
                              '{{ApplicationRole}}',
                              role_definition.oid,
                              'MEMBER')
                    LOOP
                        EXECUTE format(
                            'ALTER DEFAULT PRIVILEGES FOR ROLE %I REVOKE ALL PRIVILEGES ON %s FROM %I',
                            current_user,
                            object_kind,
                            inherited_role.rolname);
                    END LOOP;
                END LOOP;
            END
            $tabruk$;
            """,
            ApplySafeDefaultPrivilegesSql);

    public static string ApplyRuntimeLeastPrivilegeSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
        BEGIN
            EXECUTE format(
                'REVOKE ALL PRIVILEGES ON TABLE %s FROM %I, PUBLIC',
                (
                    SELECT string_agg(format('%I.%I', target_schema, managed_table.table_name), ', ')
                    FROM unnest({{SqlTextArray(ManagedTableNames)}})
                        AS managed_table(table_name)
                ),
                '{{ApplicationRole}}');
            EXECUTE format(
                'REVOKE ALL PRIVILEGES ON SEQUENCE %s FROM %I, PUBLIC',
                (
                    SELECT string_agg(format('%I.%I', target_schema, managed_sequence.sequence_name), ', ')
                    FROM unnest({{SqlTextArray(UsageSelectSequenceNames)}})
                        AS managed_sequence(sequence_name)
                ),
                '{{ApplicationRole}}');
            EXECUTE format(
                'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE %s TO %I',
                (
                    SELECT string_agg(format('%I.%I', target_schema, managed_table.table_name), ', ')
                    FROM unnest({{SqlTextArray(CrudTableNames)}})
                        AS managed_table(table_name)
                ),
                '{{ApplicationRole}}');
            EXECUTE format(
                'GRANT INSERT ON TABLE %s TO %I',
                (
                    SELECT string_agg(format('%I.%I', target_schema, managed_table.table_name), ', ')
                    FROM unnest({{SqlTextArray(InsertOnlyTableNames)}})
                        AS managed_table(table_name)
                ),
                '{{ApplicationRole}}');
            EXECUTE format(
                'GRANT USAGE, SELECT ON SEQUENCE %s TO %I',
                (
                    SELECT string_agg(format('%I.%I', target_schema, managed_sequence.sequence_name), ', ')
                    FROM unnest({{SqlTextArray(UsageSelectSequenceNames)}})
                        AS managed_sequence(sequence_name)
                ),
                '{{ApplicationRole}}');
        END
        $tabruk$;
        """;

    public static string RevokeMigrationHistoryPrivilegesSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
        BEGIN
            EXECUTE format(
                'REVOKE ALL PRIVILEGES ON TABLE %I.%I FROM %I, PUBLIC',
                target_schema,
                '{{MigrationHistoryTable}}',
                '{{ApplicationRole}}');
        END
        $tabruk$;
        """;

    public static string MigrationHistoryCanonicalQuerySql { get; } =
        $$"""
        SELECT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_namespace AS target_namespace
            JOIN pg_catalog.pg_class AS history_relation
              ON history_relation.relnamespace = target_namespace.oid
            JOIN pg_catalog.pg_am AS table_access_method
              ON table_access_method.oid = history_relation.relam
            WHERE target_namespace.nspname =
                      pg_catalog.current_setting('tabruk.target_schema', true)
              AND history_relation.relname = '{{MigrationHistoryTable}}'
              AND history_relation.relkind = 'r'
              AND history_relation.relpersistence = 'p'
              AND table_access_method.amname = 'heap'
              AND history_relation.relowner =
                  (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)
              AND NOT history_relation.relispartition
              AND history_relation.relpartbound IS NULL
              AND NOT history_relation.relrowsecurity
              AND NOT history_relation.relforcerowsecurity
              AND NOT history_relation.relhasrules
              AND NOT history_relation.relhassubclass
              AND history_relation.relchecks = 0
              AND history_relation.relreplident = 'd'
              AND COALESCE(cardinality(history_relation.reloptions), 0) = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_inherits AS inheritance
                  WHERE inheritance.inhrelid = history_relation.oid
                     OR inheritance.inhparent = history_relation.oid)
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_policy AS policy_definition
                  WHERE policy_definition.polrelid = history_relation.oid)
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_rewrite AS rule_definition
                  WHERE rule_definition.ev_class = history_relation.oid)
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_trigger AS trigger_definition
                  WHERE trigger_definition.tgrelid = history_relation.oid
                    AND NOT trigger_definition.tgisinternal)
              AND (
                  SELECT count(*)
                  FROM pg_catalog.pg_attribute AS attribute
                  WHERE attribute.attrelid = history_relation.oid
                    AND attribute.attnum > 0
                    AND NOT attribute.attisdropped
              ) = 2
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_attribute AS attribute
                  JOIN pg_catalog.pg_type AS attribute_type
                    ON attribute_type.oid = attribute.atttypid
                  WHERE attribute.attrelid = history_relation.oid
                    AND attribute.attnum = 1
                    AND attribute.attname = 'MigrationId'
                    AND attribute.atttypid = 'pg_catalog.varchar'::regtype
                    AND attribute.atttypmod = 154
                    AND attribute.attnotnull
                    AND NOT attribute.atthasdef
                    AND attribute.attidentity = ''
                    AND attribute.attgenerated = ''
                    AND NOT attribute.attisdropped
                    AND attribute.attcollation = attribute_type.typcollation)
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_attribute AS attribute
                  JOIN pg_catalog.pg_type AS attribute_type
                    ON attribute_type.oid = attribute.atttypid
                  WHERE attribute.attrelid = history_relation.oid
                    AND attribute.attnum = 2
                    AND attribute.attname = 'ProductVersion'
                    AND attribute.atttypid = 'pg_catalog.varchar'::regtype
                    AND attribute.atttypmod = 36
                    AND attribute.attnotnull
                    AND NOT attribute.atthasdef
                    AND attribute.attidentity = ''
                    AND attribute.attgenerated = ''
                    AND NOT attribute.attisdropped
                    AND attribute.attcollation = attribute_type.typcollation)
              AND (
                  SELECT count(*)
                  FROM pg_catalog.pg_constraint AS constraint_definition
                  WHERE constraint_definition.conrelid = history_relation.oid
              ) = 3
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_constraint AS constraint_definition
                  WHERE constraint_definition.conrelid = history_relation.oid
                    AND constraint_definition.conname =
                        '__EFMigrationsHistory_MigrationId_not_null'
                    AND constraint_definition.contype = 'n'
                    AND constraint_definition.conkey = ARRAY[1]::smallint[]
                    AND constraint_definition.convalidated
                    AND constraint_definition.conenforced
                    AND constraint_definition.conislocal
                    AND constraint_definition.coninhcount = 0
                    AND NOT constraint_definition.connoinherit
                    AND constraint_definition.conparentid = 0
                    AND NOT constraint_definition.condeferrable
                    AND NOT constraint_definition.condeferred
                    AND constraint_definition.conindid = 0)
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_constraint AS constraint_definition
                  WHERE constraint_definition.conrelid = history_relation.oid
                    AND constraint_definition.conname =
                        '__EFMigrationsHistory_ProductVersion_not_null'
                    AND constraint_definition.contype = 'n'
                    AND constraint_definition.conkey = ARRAY[2]::smallint[]
                    AND constraint_definition.convalidated
                    AND constraint_definition.conenforced
                    AND constraint_definition.conislocal
                    AND constraint_definition.coninhcount = 0
                    AND NOT constraint_definition.connoinherit
                    AND constraint_definition.conparentid = 0
                    AND NOT constraint_definition.condeferrable
                    AND NOT constraint_definition.condeferred
                    AND constraint_definition.conindid = 0)
              AND EXISTS (
                  SELECT 1
                  FROM pg_catalog.pg_constraint AS constraint_definition
                  JOIN pg_catalog.pg_index AS index_definition
                    ON index_definition.indexrelid = constraint_definition.conindid
                  JOIN pg_catalog.pg_class AS index_relation
                    ON index_relation.oid = index_definition.indexrelid
                  JOIN pg_catalog.pg_am AS index_access_method
                    ON index_access_method.oid = index_relation.relam
                  WHERE constraint_definition.conrelid = history_relation.oid
                    AND constraint_definition.conname = 'PK___EFMigrationsHistory'
                    AND constraint_definition.contype = 'p'
                    AND constraint_definition.conkey = ARRAY[1]::smallint[]
                    AND constraint_definition.convalidated
                    AND constraint_definition.conenforced
                    AND constraint_definition.conislocal
                    AND constraint_definition.coninhcount = 0
                    AND constraint_definition.connoinherit
                    AND constraint_definition.conparentid = 0
                    AND NOT constraint_definition.condeferrable
                    AND NOT constraint_definition.condeferred
                    AND index_relation.relnamespace = history_relation.relnamespace
                    AND index_relation.relname = 'PK___EFMigrationsHistory'
                    AND index_relation.relkind = 'i'
                    AND index_relation.relpersistence = 'p'
                    AND index_relation.relowner = history_relation.relowner
                    AND index_access_method.amname = 'btree'
                    AND COALESCE(cardinality(index_relation.reloptions), 0) = 0
                    AND index_definition.indrelid = history_relation.oid
                    AND index_definition.indnatts = 1
                    AND index_definition.indnkeyatts = 1
                    AND index_definition.indisunique
                    AND index_definition.indisprimary
                    AND NOT index_definition.indisexclusion
                    AND index_definition.indimmediate
                    AND NOT index_definition.indisclustered
                    AND index_definition.indisvalid
                    AND index_definition.indisready
                    AND index_definition.indislive
                    AND NOT index_definition.indisreplident
                    AND NOT index_definition.indnullsnotdistinct
                    AND index_definition.indkey::text = '1'
                    AND index_definition.indoption::text = '0'
                    AND index_definition.indcollation::text = (
                        SELECT attribute.attcollation::text
                        FROM pg_catalog.pg_attribute AS attribute
                        WHERE attribute.attrelid = history_relation.oid
                          AND attribute.attnum = 1)
                    AND index_definition.indclass::text = (
                        SELECT operator_class.oid::text
                        FROM pg_catalog.pg_opclass AS operator_class
                        JOIN pg_catalog.pg_namespace AS operator_namespace
                          ON operator_namespace.oid = operator_class.opcnamespace
                        JOIN pg_catalog.pg_am AS operator_access_method
                          ON operator_access_method.oid = operator_class.opcmethod
                        WHERE operator_namespace.nspname = 'pg_catalog'
                          AND operator_access_method.amname = 'btree'
                          AND operator_class.opcname = 'text_ops')
                    AND index_definition.indexprs IS NULL
                    AND index_definition.indpred IS NULL)
              AND (
                  SELECT count(*)
                  FROM pg_catalog.pg_index AS index_definition
                  WHERE index_definition.indrelid = history_relation.oid
              ) = 1
        )
        """;

    public static string MigrationHistoryStructureAttestationSql { get; } =
        $$"""
        DO $tabruk$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS history_relation
                JOIN pg_catalog.pg_namespace AS history_namespace
                  ON history_namespace.oid = history_relation.relnamespace
                WHERE history_namespace.nspname =
                          pg_catalog.current_setting('tabruk.target_schema', true)
                  AND history_relation.relname = '{{MigrationHistoryTable}}'
            )
                AND NOT ({{MigrationHistoryCanonicalQuerySql}})
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: history-structure-mismatch. T8_ATTESTATION_FAILED:history.';
            END IF;
        END
        $tabruk$;
        """;

    public static string RuntimePrivilegeAttestationSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            -- Canonical T8 categories retained as explicit audit vocabulary:
            -- category=identity. category=owner. category=state. category=flags.
            -- category=key-count. category=key-order. category=key-types.
            -- category=operator-classes. category=key-options. category=relation-options.
            -- category=constraint-attachment. category=predicate. category=type.
            -- category=inheritance. category=expression.
            target_namespace oid;
            privilege_mismatch boolean;
        BEGIN
            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname =
                pg_catalog.current_setting('tabruk.target_schema', true);

            SELECT EXISTS (
                SELECT 1
                FROM unnest({{SqlTextArray(CrudTableNames)}}) AS managed_table(table_name)
                CROSS JOIN unnest({{SqlTextArray(CrudTablePrivilegeNames)}})
                    AS required_privilege(privilege_name)
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relnamespace = target_namespace
                 AND relation.relname = managed_table.table_name
                WHERE NOT pg_catalog.has_table_privilege(
                    '{{ApplicationRole}}', relation.oid, required_privilege.privilege_name)
            ) OR EXISTS (
                SELECT 1
                FROM unnest({{SqlTextArray(CrudTableNames)}}) AS managed_table(table_name)
                CROSS JOIN unnest({{SqlTextArray(CrudDeniedTablePrivilegeNames)}})
                    AS denied_privilege(privilege_name)
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relnamespace = target_namespace
                 AND relation.relname = managed_table.table_name
                WHERE pg_catalog.has_table_privilege(
                    '{{ApplicationRole}}', relation.oid, denied_privilege.privilege_name)
            ) OR EXISTS (
                SELECT 1
                FROM unnest({{SqlTextArray(CrudTableNames)}}) AS managed_table(table_name)
                CROSS JOIN unnest({{SqlTextArray(CrudDeniedColumnPrivilegeNames)}})
                    AS denied_privilege(privilege_name)
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relnamespace = target_namespace
                 AND relation.relname = managed_table.table_name
                WHERE pg_catalog.has_any_column_privilege(
                    '{{ApplicationRole}}', relation.oid, denied_privilege.privilege_name)
            ) OR EXISTS (
                SELECT 1
                FROM unnest({{SqlTextArray(InsertOnlyTableNames)}}) AS managed_table(table_name)
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relnamespace = target_namespace
                 AND relation.relname = managed_table.table_name
                WHERE NOT pg_catalog.has_table_privilege(
                    '{{ApplicationRole}}', relation.oid, 'INSERT')
                   OR EXISTS (
                       SELECT 1
                       FROM unnest({{SqlTextArray(InsertOnlyDeniedTablePrivilegeNames)}})
                           AS denied_privilege(privilege_name)
                       WHERE pg_catalog.has_table_privilege(
                           '{{ApplicationRole}}', relation.oid, denied_privilege.privilege_name))
            ) OR EXISTS (
                SELECT 1
                FROM unnest({{SqlTextArray(InsertOnlyTableNames)}}) AS managed_table(table_name)
                CROSS JOIN unnest({{SqlTextArray(InsertOnlyDeniedColumnPrivilegeNames)}})
                    AS denied_privilege(privilege_name)
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relnamespace = target_namespace
                 AND relation.relname = managed_table.table_name
                WHERE pg_catalog.has_any_column_privilege(
                    '{{ApplicationRole}}', relation.oid, denied_privilege.privilege_name)
            ) OR EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                CROSS JOIN unnest({{SqlTextArray(PostgreSql18TablePrivilegeNames)}})
                    AS denied_privilege(privilege_name)
                WHERE relation.relnamespace = target_namespace
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND NOT relation.relname = ANY({{SqlTextArray(ClassifiedTableNames)}})
                  AND pg_catalog.has_table_privilege(
                      '{{ApplicationRole}}', relation.oid, denied_privilege.privilege_name)
            ) OR EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                CROSS JOIN unnest({{SqlTextArray(PostgreSql18ColumnPrivilegeNames)}})
                    AS denied_privilege(privilege_name)
                WHERE relation.relnamespace = target_namespace
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND NOT relation.relname = ANY({{SqlTextArray(ClassifiedTableNames)}})
                  AND pg_catalog.has_any_column_privilege(
                      '{{ApplicationRole}}', relation.oid, denied_privilege.privilege_name)
            ) OR EXISTS (
                SELECT 1
                FROM unnest({{SqlTextArray(UsageSelectSequenceNames)}})
                    AS managed_sequence(sequence_name)
                CROSS JOIN unnest(ARRAY['USAGE', 'SELECT'])
                    AS required_privilege(privilege_name)
                JOIN pg_catalog.pg_class AS relation
                  ON relation.relnamespace = target_namespace
                 AND relation.relname = managed_sequence.sequence_name
                WHERE NOT pg_catalog.has_sequence_privilege(
                    '{{ApplicationRole}}', relation.oid, required_privilege.privilege_name)
                   OR pg_catalog.has_sequence_privilege(
                       '{{ApplicationRole}}', relation.oid, 'UPDATE')
            ) OR EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                    COALESCE(
                        relation.relacl,
                        pg_catalog.acldefault(
                            CASE WHEN relation.relkind = 'S' THEN 'S'::"char" ELSE 'r'::"char" END,
                            relation.relowner))) AS privilege
                WHERE relation.relnamespace = target_namespace
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
                  AND privilege.grantee = 0
            ) OR EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS privilege
                WHERE relation.relnamespace = target_namespace
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND privilege.grantee = 0
            ) OR EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                    COALESCE(
                        relation.relacl,
                        pg_catalog.acldefault(
                            CASE WHEN relation.relkind = 'S' THEN 'S'::"char" ELSE 'r'::"char" END,
                            relation.relowner))) AS privilege
                WHERE relation.relnamespace = target_namespace
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
                  AND privilege.is_grantable
                  AND privilege.grantee <> 0
                  AND (
                      privilege.grantee =
                          (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = '{{ApplicationRole}}')
                      OR pg_catalog.pg_has_role('{{ApplicationRole}}', privilege.grantee, 'MEMBER')
                  )
            ) OR EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_attribute AS attribute
                  ON attribute.attrelid = relation.oid
                CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS privilege
                WHERE relation.relnamespace = target_namespace
                  AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                  AND attribute.attnum > 0
                  AND NOT attribute.attisdropped
                  AND (
                      privilege.grantee = 0
                      OR privilege.grantee =
                          (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = '{{ApplicationRole}}')
                      OR pg_catalog.pg_has_role('{{ApplicationRole}}', privilege.grantee, 'MEMBER')
                  )
            )
            INTO privilege_mismatch;

            IF privilege_mismatch THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
            END IF;
        END
        $tabruk$;
        """;

    public static string MigrationHistoryPrivilegeAttestationSql { get; } =
        RuntimePrivilegeAttestationSql;

    public static string EmptyBootstrapHistoryAttestationSql { get; } =
        """
        DO $tabruk$
        DECLARE
            target_schema text := current_setting('tabruk.target_schema', true);
            history_count integer;
        BEGIN
            EXECUTE format(
                'SELECT count(*) FROM %I.%I',
                target_schema,
                '__EFMigrationsHistory')
            INTO history_count;
            IF history_count <> 0 THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: partial-initial-inventory. T8_ATTESTATION_FAILED:history.';
            END IF;
        END
        $tabruk$;
        """;

    public static string ClassifyInitialInventorySql { get; } =
        $$"""
        SELECT CASE
            WHEN classified_count = 0
              AND sequence_count = 0
              AND NOT history_present
              AND NOT waitlist_index_present
              AND NOT chronology_constraint_present
                THEN 'pristine'
            WHEN classified_count = 0
              AND sequence_count = 0
              AND history_canonical
              AND NOT waitlist_index_present
              AND NOT chronology_constraint_present
                THEN 'ef-bootstrap'
            WHEN classified_count = 0
              AND sequence_count = 0
              AND history_table_like
              AND NOT waitlist_index_present
              AND NOT chronology_constraint_present
                THEN 'malformed-ef-bootstrap'
            WHEN classified_count = {{ClassifiedTableNames.Length}}
              AND sequence_count = {{UsageSelectSequenceNames.Length}}
              AND history_present
                THEN 'initial-inventory'
            ELSE 'invalid'
        END
        FROM (
            SELECT
                (
                    SELECT count(*)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname =
                              pg_catalog.current_setting('tabruk.target_schema', true)
                      AND relation.relname = ANY({{SqlTextArray(ClassifiedTableNames)}})
                      AND relation.relkind = 'r'
                ) AS classified_count,
                (
                    SELECT count(*)
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname =
                              pg_catalog.current_setting('tabruk.target_schema', true)
                      AND relation.relname = ANY({{SqlTextArray(UsageSelectSequenceNames)}})
                      AND relation.relkind = 'S'
                ) AS sequence_count,
                EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname =
                              pg_catalog.current_setting('tabruk.target_schema', true)
                      AND relation.relname = '{{MigrationHistoryTable}}'
                ) AS history_present,
                EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname =
                              pg_catalog.current_setting('tabruk.target_schema', true)
                      AND relation.relname = '{{MigrationHistoryTable}}'
                      AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
                ) AS history_table_like,
                ({{MigrationHistoryCanonicalQuerySql}}) AS history_canonical,
                EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname =
                              pg_catalog.current_setting('tabruk.target_schema', true)
                      AND relation.relname = '{{WaitlistIndex}}'
                ) AS waitlist_index_present,
                EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_constraint AS constraint_definition
                    JOIN pg_catalog.pg_class AS relation
                      ON relation.oid = constraint_definition.conrelid
                    JOIN pg_catalog.pg_namespace AS namespace_definition
                      ON namespace_definition.oid = relation.relnamespace
                    WHERE namespace_definition.nspname =
                              pg_catalog.current_setting('tabruk.target_schema', true)
                      AND relation.relname = 'signups'
                      AND constraint_definition.conname = '{{ChronologyConstraint}}'
                ) AS chronology_constraint_present
        ) AS inventory;
        """;

    public static string CorrectiveObjectPreflightSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            -- index_relation.relowner = table_relation.relowner
            -- ARRAY['uuid', 'uuid', 'bigint']
            -- ARRAY['uuid_ops', 'uuid_ops', 'int8_ops']
            -- index_definition.indcollation::text = '0 0 0'
            -- index_definition.indoption::text = '0 0 0'
            -- COALESCE(cardinality(index_relation.reloptions), 0) = 0
            -- NOT index_definition.indnullsnotdistinct
            -- NOT index_definition.indisprimary
            -- NOT index_definition.indisexclusion
            -- index_definition.indimmediate
            -- NOT index_definition.indisclustered
            -- NOT index_definition.indisreplident
            -- WHERE conindid = index_oid
            -- constraint_definition.conislocal
            -- constraint_definition.coninhcount = 0
            -- constraint_definition.conparentid = 0
            -- constraint_definition.conenforced
            -- constraint_definition.convalidated is enforced by ValidatedCorrectiveObjectPreflightSql
            -- Canonical T8 categories retained as explicit audit vocabulary:
            -- category=identity. category=owner. category=state. category=flags.
            -- category=key-count. category=key-order. category=key-types.
            -- category=operator-classes. category=key-options. category=relation-options.
            -- category=constraint-attachment. category=predicate. category=type.
            -- category=inheritance. category=expression.
            target_namespace oid;
            signups_oid oid;
            history_owner oid;
            index_oid oid;
            constraint_oid oid;
        BEGIN
            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname =
                pg_catalog.current_setting('tabruk.target_schema', true);

            SELECT relation.oid, relation.relowner
            INTO signups_oid, history_owner
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = 'signups'
              AND relation.relkind = 'r';

            SELECT relation.oid
            INTO index_oid
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = '{{WaitlistIndex}}';

            IF index_oid IS NOT NULL THEN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    JOIN pg_catalog.pg_class AS index_relation
                      ON index_relation.oid = index_definition.indexrelid
                    JOIN pg_catalog.pg_class AS table_relation
                      ON table_relation.oid = index_definition.indrelid
                    JOIN pg_catalog.pg_am AS access_method
                      ON access_method.oid = index_relation.relam
                    WHERE index_definition.indexrelid = index_oid
                      AND index_relation.relnamespace = target_namespace
                      AND index_relation.relkind = 'i'
                      AND table_relation.oid = signups_oid
                      AND access_method.amname = 'btree')
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=identity.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    WHERE index_definition.indexrelid = index_oid
                      AND index_definition.indisunique
                      AND index_definition.indisvalid
                      AND index_definition.indisready
                      AND index_definition.indislive)
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=state.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    WHERE index_definition.indexrelid = index_oid
                      AND NOT index_definition.indisprimary
                      AND NOT index_definition.indisexclusion
                      AND index_definition.indimmediate
                      AND NOT index_definition.indisclustered
                      AND NOT index_definition.indisreplident
                      AND NOT index_definition.indnullsnotdistinct)
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=flags.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    WHERE index_definition.indexrelid = index_oid
                      AND index_definition.indnkeyatts = 3
                      AND index_definition.indnatts = 3
                      AND index_definition.indexprs IS NULL)
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=key-count.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    WHERE index_definition.indexrelid = index_oid
                      AND (
                          SELECT array_agg(attribute.attname::text ORDER BY indexed_attribute.ordinality)
                          FROM unnest(index_definition.indkey)
                              WITH ORDINALITY AS indexed_attribute(attribute_number, ordinality)
                          JOIN pg_catalog.pg_attribute AS attribute
                            ON attribute.attrelid = signups_oid
                           AND attribute.attnum = indexed_attribute.attribute_number
                          WHERE indexed_attribute.ordinality <= index_definition.indnkeyatts
                      ) = ARRAY['organization_id', 'help_need_id', 'waitlist_order'])
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=key-order.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    WHERE index_definition.indexrelid = index_oid
                      AND index_definition.indcollation::text = '0 0 0'
                      AND index_definition.indoption::text = '0 0 0')
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=key-options.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS index_relation
                    WHERE index_relation.oid = index_oid
                      AND COALESCE(cardinality(index_relation.reloptions), 0) = 0)
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=relation-options.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    WHERE index_definition.indexrelid = index_oid
                      AND pg_catalog.pg_get_expr(index_definition.indpred, signups_oid) = '(status = 2)')
                THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=predicate.; T8_ATTESTATION_FAILED:object.';
                END IF;
            END IF;

            IF index_oid IS NOT NULL THEN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    JOIN pg_catalog.pg_class AS index_relation
                      ON index_relation.oid = index_definition.indexrelid
                    JOIN pg_catalog.pg_class AS table_relation
                      ON table_relation.oid = index_definition.indrelid
                    JOIN pg_catalog.pg_am AS access_method
                      ON access_method.oid = index_relation.relam
                    WHERE index_definition.indexrelid = index_oid
                      AND index_relation.relnamespace = target_namespace
                      AND index_relation.relkind = 'i'
                      AND table_relation.oid = signups_oid
                      AND access_method.amname = 'btree'
                ) THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=identity.; T8_ATTESTATION_FAILED:object.';
                END IF;
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_index AS index_definition
                    JOIN pg_catalog.pg_class AS index_relation
                      ON index_relation.oid = index_definition.indexrelid
                    WHERE index_definition.indexrelid = index_oid
                      AND index_relation.relowner = history_owner
                      AND index_definition.indisunique
                      AND index_definition.indisvalid
                      AND index_definition.indisready
                      AND index_definition.indislive
                      AND NOT index_definition.indisprimary
                      AND NOT index_definition.indisexclusion
                      AND index_definition.indimmediate
                      AND NOT index_definition.indisclustered
                      AND NOT index_definition.indisreplident
                      AND NOT index_definition.indnullsnotdistinct
                      AND index_definition.indnkeyatts = 3
                      AND index_definition.indnatts = 3
                      AND index_definition.indexprs IS NULL
                      AND index_definition.indcollation::text = '0 0 0'
                      AND index_definition.indoption::text = '0 0 0'
                      AND COALESCE(cardinality(index_relation.reloptions), 0) = 0
                      AND NOT EXISTS (
                          SELECT 1
                          FROM pg_catalog.pg_constraint AS constraint_definition
                          WHERE constraint_definition.conindid = index_oid)
                      AND (
                          SELECT array_agg(attribute.attname::text ORDER BY indexed_attribute.ordinality)
                          FROM unnest(index_definition.indkey)
                              WITH ORDINALITY AS indexed_attribute(attribute_number, ordinality)
                          JOIN pg_catalog.pg_attribute AS attribute
                            ON attribute.attrelid = signups_oid
                           AND attribute.attnum = indexed_attribute.attribute_number
                          WHERE indexed_attribute.ordinality <= index_definition.indnkeyatts
                      ) = ARRAY['organization_id', 'help_need_id', 'waitlist_order']
                      AND (
                          SELECT array_agg(attribute.atttypid::regtype::text ORDER BY indexed_attribute.ordinality)
                          FROM unnest(index_definition.indkey)
                              WITH ORDINALITY AS indexed_attribute(attribute_number, ordinality)
                          JOIN pg_catalog.pg_attribute AS attribute
                            ON attribute.attrelid = signups_oid
                           AND attribute.attnum = indexed_attribute.attribute_number
                          WHERE indexed_attribute.ordinality <= index_definition.indnkeyatts
                      ) = ARRAY['uuid', 'uuid', 'bigint']
                      AND (
                          SELECT array_agg(operator_class.opcname::text ORDER BY operator_attribute.ordinality)
                          FROM unnest(index_definition.indclass)
                              WITH ORDINALITY AS operator_attribute(operator_class_oid, ordinality)
                          JOIN pg_catalog.pg_opclass AS operator_class
                            ON operator_class.oid = operator_attribute.operator_class_oid
                          JOIN pg_catalog.pg_namespace AS operator_namespace
                            ON operator_namespace.oid = operator_class.opcnamespace
                          WHERE operator_namespace.nspname = 'pg_catalog'
                            AND operator_class.opcmethod = index_relation.relam
                      ) = ARRAY['uuid_ops', 'uuid_ops', 'int8_ops']
                      AND pg_catalog.pg_get_expr(index_definition.indpred, signups_oid) =
                          '(status = 2)'
                ) THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 object definition mismatch: object={{WaitlistIndex}}; category=state.; T8_ATTESTATION_FAILED:object.';
                END IF;
            END IF;

            SELECT constraint_definition.oid
            INTO constraint_oid
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.conrelid = signups_oid
              AND constraint_definition.conname = '{{ChronologyConstraint}}';

            IF constraint_oid IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_constraint AS constraint_definition
                    WHERE constraint_definition.oid = constraint_oid
                      AND constraint_definition.contype = 'c'
                      AND constraint_definition.conenforced
                      AND constraint_definition.conislocal
                      AND constraint_definition.coninhcount = 0
                      AND constraint_definition.conparentid = 0
                      AND NOT constraint_definition.connoinherit)
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 object definition mismatch: object={{ChronologyConstraint}}; category=type.; T8_ATTESTATION_FAILED:object.';
            END IF;

            IF constraint_oid IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_constraint AS constraint_definition
                    WHERE constraint_definition.oid = constraint_oid
                      AND constraint_definition.contype = 'c'
                      AND constraint_definition.conenforced
                      AND constraint_definition.conislocal
                      AND constraint_definition.coninhcount = 0
                      AND constraint_definition.conparentid = 0
                      AND NOT constraint_definition.connoinherit
                      AND pg_catalog.pg_get_expr(
                          constraint_definition.conbin,
                          constraint_definition.conrelid) =
                              '((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))'
                )
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 object definition mismatch: object={{ChronologyConstraint}}; category=expression.; T8_ATTESTATION_FAILED:object.';
            END IF;
        END
        $tabruk$;
        """;

    public static string ValidatedCorrectiveObjectPreflightSql { get; } =
        $$"""
        {{CorrectiveObjectPreflightSql}}
        DO $tabruk$
        DECLARE
            target_namespace oid;
            signups_oid oid;
        BEGIN
            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname =
                pg_catalog.current_setting('tabruk.target_schema', true);
            SELECT relation.oid
            INTO signups_oid
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = 'signups';
            IF EXISTS (
                SELECT 1
                FROM pg_catalog.pg_constraint AS constraint_definition
                WHERE constraint_definition.conrelid = signups_oid
                  AND constraint_definition.conname = '{{ChronologyConstraint}}'
                  AND NOT constraint_definition.convalidated
            )
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 object definition mismatch: object={{ChronologyConstraint}}; category=state.; T8_ATTESTATION_FAILED:object.';
            END IF;
        END
        $tabruk$;
        """;

    public static string ManagedObjectOwnerAttestationSql { get; } =
        $$"""
        DO $tabruk$
        DECLARE
            target_namespace oid;
            owner_oid oid;
        BEGIN
            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname =
                pg_catalog.current_setting('tabruk.target_schema', true);
            SELECT oid
            INTO owner_oid
            FROM pg_catalog.pg_roles
            WHERE rolname = current_user;

            IF EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                WHERE relation.relnamespace = target_namespace
                  AND relation.relname = ANY(
                      {{SqlTextArray([.. ManagedTableNames, .. UsageSelectSequenceNames])}})
                  AND relation.relowner <> owner_oid
            ) THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: initial-owner-mismatch. T8_ATTESTATION_FAILED:owner.';
            END IF;
        END
        $tabruk$;
        """;

    public static string LockedCanonicalReattestationSql =>
        string.Join(
            Environment.NewLine,
            SchemaPreflightSql,
            SchemaOwnerAndAclAttestationSql,
            MigrationHistoryStructureAttestationSql,
            ManagedTableTopologyAttestationSql,
            ManagedObjectOwnerAttestationSql,
            RuntimePrivilegeAttestationSql,
            ValidatedCorrectiveObjectPreflightSql);

    public static string ApplyRuntimeLeastPrivilegeUnderTopologyLockSql =>
        string.Join(
            Environment.NewLine,
            "BEGIN;",
            ManagedTableTopologyLockSql,
            ApplySafeDisposableDefaultPrivilegesSql,
            SchemaOwnerAndAclAttestationSql,
            MigrationHistoryStructureAttestationSql,
            ManagedTableTopologyAttestationSql,
            ManagedObjectOwnerAttestationSql,
            ApplyRuntimeLeastPrivilegeSql,
            RuntimePrivilegeAttestationSql,
            "COMMIT;");

    public static string ApplyRuntimeLeastPrivilegeAndCanonicalReattestationUnderTopologyLockSql =>
        string.Join(
            Environment.NewLine,
            "BEGIN;",
            ManagedTableTopologyLockSql,
            ApplySafeDisposableDefaultPrivilegesSql,
            SchemaOwnerAndAclAttestationSql,
            MigrationHistoryStructureAttestationSql,
            ManagedTableTopologyAttestationSql,
            ManagedObjectOwnerAttestationSql,
            ApplyRuntimeLeastPrivilegeSql,
            RuntimePrivilegeAttestationSql,
            CorrectiveObjectPreflightSql,
            "COMMIT;");

    public static string PreHistoryAttestationSql { get; } =
        $$"""
        {{ApplySafeDisposableDefaultPrivilegesSql}}
        {{SchemaOwnerAndAclAttestationSql}}
        {{MigrationHistoryStructureAttestationSql}}
        {{ManagedTableTopologyAttestationSql}}
        {{ManagedObjectOwnerAttestationSql}}
        {{RuntimePrivilegeAttestationSql}}
        {{CorrectiveObjectPreflightSql}}
        -- T8 pre-history attestation failed: corrective-history-object-mismatch.

        DO $tabruk$
        DECLARE
            target_namespace oid;
            history_relation oid;
            history_count integer;
            unknown_history_count integer;
            initial_applied boolean;
            corrective_applied boolean;
            index_present boolean;
            constraint_present boolean;
            constraint_validated boolean;
        BEGIN
            SELECT namespace_definition.oid
            INTO target_namespace
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.nspname =
                pg_catalog.current_setting('tabruk.target_schema', true);

            SELECT relation.oid
            INTO history_relation
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = '{{MigrationHistoryTable}}';

            IF history_relation IS NULL THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: partial-initial-inventory. T8_ATTESTATION_FAILED:history.';
            END IF;

            EXECUTE format(
                'SELECT count(*), count(*) FILTER (WHERE "MigrationId" NOT IN (%L, %L)), COALESCE(bool_or("MigrationId" = %L), false), COALESCE(bool_or("MigrationId" = %L), false) FROM %I.%I',
                '{{InitialMigrationId}}',
                '{{CorrectiveMigrationId}}',
                '{{InitialMigrationId}}',
                '{{CorrectiveMigrationId}}',
                pg_catalog.current_setting('tabruk.target_schema', true),
                '{{MigrationHistoryTable}}')
            INTO history_count, unknown_history_count, initial_applied, corrective_applied;

            IF history_count NOT IN (1, 2)
                OR unknown_history_count <> 0
                OR NOT initial_applied THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: unknown-history. T8_ATTESTATION_FAILED:history.';
            END IF;

            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                WHERE relation.relnamespace = target_namespace
                  AND relation.relname = '{{WaitlistIndex}}'
            ),
            EXISTS (
                SELECT 1
                FROM pg_catalog.pg_constraint AS constraint_definition
                WHERE constraint_definition.conrelid = (
                    SELECT relation.oid
                    FROM pg_catalog.pg_class AS relation
                    WHERE relation.relnamespace = target_namespace
                      AND relation.relname = 'signups'
                )
                  AND constraint_definition.conname = '{{ChronologyConstraint}}'
            )
            INTO index_present, constraint_present;

            IF corrective_applied AND (NOT index_present OR NOT constraint_present) THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: corrective-history-object-mismatch. T8_ATTESTATION_FAILED:history.';
            END IF;

            IF corrective_applied THEN
                SELECT constraint_definition.convalidated
                INTO constraint_validated
                FROM pg_catalog.pg_constraint AS constraint_definition
                WHERE constraint_definition.conrelid = (
                    SELECT relation.oid
                    FROM pg_catalog.pg_class AS relation
                    WHERE relation.relnamespace = target_namespace
                      AND relation.relname = 'signups'
                )
                  AND constraint_definition.conname = '{{ChronologyConstraint}}';

                IF NOT constraint_validated THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001',
                        MESSAGE = 'T8 pre-history attestation failed: corrective-history-validation-mismatch. T8_ATTESTATION_FAILED:history.';
                END IF;
            END IF;
        END
        $tabruk$;
        """;

    private static string JoinSchemaQualifiedIdentifiers(
        string schema,
        IEnumerable<string> identifiers) =>
        string.Join(
            ", ",
            identifiers.Select(
                identifier => $"{QuoteIdentifier(schema)}.{QuoteIdentifier(identifier)}"));

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string SqlTextArray(IEnumerable<string> values) =>
        $"ARRAY[{string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"))}]";
}
