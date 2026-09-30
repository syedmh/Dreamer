\set ON_ERROR_STOP on

-- Production T8 upgrade.  Run with psql -X and autocommit; never use --single-transaction.
-- The session advisory lock is deliberately never unlocked by this file.  Disconnecting psql
-- releases it after the final history commit.
\if :{?target_schema}
\else
    \echo T8_RESULT=failed
    DO $fail$
    BEGIN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'target_schema is required';
    END
    $fail$;
\endif

SET tabruk.target_schema TO :'target_schema';
SELECT pg_catalog.set_config('search_path', 'pg_catalog', false);

DO $tabruk$
DECLARE
    target_schema text := pg_catalog.current_setting('tabruk.target_schema', true);
    target_namespace oid;
    raw_search_path text := pg_catalog.current_setting('search_path');
BEGIN
    SELECT namespace_definition.oid
    INTO target_namespace
    FROM pg_catalog.pg_namespace AS namespace_definition
    WHERE namespace_definition.nspname = target_schema;

    IF target_schema IS NULL
        OR target_schema !~ '^[a-z_][a-z0-9_$]{0,62}$'
        OR target_schema IN ('pg_catalog', 'information_schema')
        OR target_schema LIKE 'pg\_%' ESCAPE '\'
        OR raw_search_path <> 'pg_catalog'
        OR target_namespace IS NULL
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 schema contract mismatch: target_schema/search_path; T8_ATTESTATION_FAILED:schema.';
    END IF;
END
$tabruk$;

CREATE TEMP TABLE pg_temp.t8_session_state
(
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    target_schema name NOT NULL,
    target_namespace oid NOT NULL,
    owner_oid oid NOT NULL
) ON COMMIT PRESERVE ROWS;
INSERT INTO pg_temp.t8_session_state
    (target_schema, target_namespace, owner_oid)
SELECT namespace_definition.nspname, namespace_definition.oid, namespace_definition.nspowner
FROM pg_catalog.pg_namespace AS namespace_definition
WHERE namespace_definition.nspname = :'target_schema';

CREATE OR REPLACE FUNCTION pg_temp.t8_target_namespace()
RETURNS oid
LANGUAGE plpgsql
AS $body$
DECLARE
    session_state pg_temp.t8_session_state%ROWTYPE;
BEGIN
    SELECT state.*
    INTO STRICT session_state
    FROM pg_temp.t8_session_state AS state
    WHERE state.singleton;

    IF session_state.target_schema::text
            IS DISTINCT FROM pg_catalog.current_setting('tabruk.target_schema', true)
        OR session_state.owner_oid
            <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.oid = session_state.target_namespace
              AND namespace_definition.nspname = session_state.target_schema
              AND namespace_definition.nspowner = session_state.owner_oid)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 schema identity changed during execution. T8_ATTESTATION_FAILED:schema.';
    END IF;

    RETURN session_state.target_namespace;
EXCEPTION
    WHEN NO_DATA_FOUND OR TOO_MANY_ROWS THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 session namespace state is invalid. T8_ATTESTATION_FAILED:schema.';
END
$body$;

SELECT pg_catalog.pg_advisory_lock(
    pg_catalog.hashtextextended(
        pg_catalog.current_database()
        || ':' || (
            SELECT session_state.target_namespace::text
            FROM pg_temp.t8_session_state AS session_state)
        || ':T8',
        0));
SELECT pg_temp.t8_target_namespace();

CREATE OR REPLACE FUNCTION pg_temp.t8_attest(
    require_corrective boolean,
    allow_unvalidated boolean)
RETURNS void
LANGUAGE plpgsql
AS $body$
DECLARE
    target_schema text := pg_catalog.current_setting('tabruk.target_schema', true);
    target_namespace oid;
    owner_oid oid;
    app_oid oid;
    relation_oid oid;
    history_oid oid;
    signups_oid oid;
    index_oid oid;
    constraint_oid oid;
    history_count integer;
    unknown_history_count integer;
    initial_applied boolean;
    corrective_applied boolean;
    constraint_validated boolean;
    relation_name text;
    privilege_name text;
    has_rows boolean;
BEGIN
    target_namespace := pg_temp.t8_target_namespace();
    SELECT session_state.owner_oid
    INTO owner_oid
    FROM pg_temp.t8_session_state AS session_state
    WHERE session_state.singleton;

    SELECT role_definition.oid
    INTO app_oid
    FROM pg_catalog.pg_roles AS role_definition
    WHERE role_definition.rolname = 'tabruk_app';

    IF target_namespace IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_namespace AS namespace_definition
            WHERE namespace_definition.oid = target_namespace
              AND namespace_definition.nspname = target_schema
              AND namespace_definition.nspowner = owner_oid)
        OR owner_oid <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)
        OR app_oid IS NULL
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.pg_roles AS role_definition
            WHERE role_definition.oid = app_oid
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
        OR NOT pg_catalog.has_schema_privilege('tabruk_app', target_namespace, 'USAGE')
        OR pg_catalog.has_schema_privilege('tabruk_app', target_namespace, 'CREATE')
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_namespace AS namespace_definition
            CROSS JOIN LATERAL pg_catalog.aclexplode(
                COALESCE(
                    namespace_definition.nspacl,
                    pg_catalog.acldefault('n', namespace_definition.nspowner)))
                AS privilege
            WHERE namespace_definition.oid = target_namespace
              AND privilege.grantee = app_oid
              AND privilege.privilege_type = 'USAGE'
              AND NOT privilege.is_grantable
        )
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.aclexplode(
                COALESCE(
                    (SELECT namespace_definition.nspacl
                     FROM pg_catalog.pg_namespace AS namespace_definition
                     WHERE namespace_definition.oid = target_namespace),
                    pg_catalog.acldefault('n', owner_oid)))
                AS privilege
            WHERE privilege.is_grantable
              AND privilege.grantee <> 0
              AND (
                  privilege.grantee = app_oid
                  OR pg_catalog.pg_has_role('tabruk_app', privilege.grantee, 'MEMBER')
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
               OR privilege.grantee = app_oid
               OR pg_catalog.pg_has_role('tabruk_app', privilege.grantee, 'MEMBER')
        )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: owner-acl. T8_ATTESTATION_FAILED:acl.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = pg_catalog.pg_my_temp_schema()
          AND relation.relname = ANY(ARRAY[
              'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
              'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
              'organizations', 'memberships', 'invitations', 'role_assignments',
              'role_change_requests', 'service_dates', 'help_needs', 'signups',
              'signup_member_participants', 'date_threads', 'thread_messages',
              'message_reports', 'thread_moderation_events', 'notifications',
              'device_registrations', 'outbox_messages', 'idempotency_records',
              'audit_events', 'privileged_access_events', '__EFMigrationsHistory',
              'identity_role_claims_Id_seq', 'identity_user_claims_Id_seq',
              'ux_signups_waitlisted_order_per_help_need'])
    )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: temp-shadow. T8_ATTESTATION_FAILED:temp.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = '__EFMigrationsHistory'
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
            MESSAGE = 'T8 pre-history attestation failed: history-structure-mismatch. T8_ATTESTATION_FAILED:history.';
    END IF;

    IF (
        (
            SELECT count(*)
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = ANY(ARRAY[
                  'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
                  'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
                  'organizations', 'memberships', 'invitations', 'role_assignments',
                  'role_change_requests', 'service_dates', 'help_needs', 'signups',
                  'signup_member_participants', 'date_threads', 'thread_messages',
                  'message_reports', 'thread_moderation_events', 'notifications',
                  'device_registrations', 'outbox_messages', 'idempotency_records',
                  'audit_events', 'privileged_access_events', '__EFMigrationsHistory'])
              AND relation.relkind = 'r'
        ) <> 27
        OR (
            SELECT count(*)
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = ANY(ARRAY[
                  'identity_role_claims_Id_seq', 'identity_user_claims_Id_seq'])
              AND relation.relkind = 'S'
        ) <> 2
    )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: partial-initial-inventory. T8_ATTESTATION_FAILED:history.';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = ANY(ARRAY[
              'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
              'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
              'organizations', 'memberships', 'invitations', 'role_assignments',
              'role_change_requests', 'service_dates', 'help_needs', 'signups',
              'signup_member_participants', 'date_threads', 'thread_messages',
              'message_reports', 'thread_moderation_events', 'notifications',
              'device_registrations', 'outbox_messages', 'idempotency_records',
              'audit_events', 'privileged_access_events', '__EFMigrationsHistory',
              'identity_role_claims_Id_seq', 'identity_user_claims_Id_seq'])
          AND relation.relowner <> owner_oid)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: initial-owner-mismatch. T8_ATTESTATION_FAILED:owner.';
    END IF;

    IF (
        SELECT count(*)
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = ANY(ARRAY[
              'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
              'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
              'organizations', 'memberships', 'invitations', 'role_assignments',
              'role_change_requests', 'service_dates', 'help_needs', 'signups',
              'signup_member_participants', 'date_threads', 'thread_messages',
              'message_reports', 'thread_moderation_events', 'notifications',
              'device_registrations', 'outbox_messages', 'idempotency_records',
              'audit_events', 'privileged_access_events', '__EFMigrationsHistory'])
          AND relation.relkind = 'r'
          AND NOT relation.relispartition
          AND relation.relpartbound IS NULL
    ) <> 27
    OR (
        SELECT count(*)
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = ANY(ARRAY[
              'identity_role_claims_Id_seq', 'identity_user_claims_Id_seq'])
          AND relation.relkind = 'S'
    ) <> 2
    OR EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = ANY(ARRAY[
              'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
              'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
              'organizations', 'memberships', 'invitations', 'role_assignments',
              'role_change_requests', 'service_dates', 'help_needs', 'signups',
              'signup_member_participants', 'date_threads', 'thread_messages',
              'message_reports', 'thread_moderation_events', 'notifications',
              'device_registrations', 'outbox_messages', 'idempotency_records',
              'audit_events', 'privileged_access_events', '__EFMigrationsHistory'])
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

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = ANY(ARRAY[
              'identity_role_claims_Id_seq', 'identity_user_claims_Id_seq'])
          AND (
              relation.relkind <> 'S'
              OR relation.relowner <> owner_oid
              OR relation.relpersistence <> 'p'
          )
    )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: initial-owner-mismatch. T8_ATTESTATION_FAILED:owner.';
    END IF;

    SELECT relation.oid
    INTO history_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = '__EFMigrationsHistory';

    IF history_oid IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: partial-initial-inventory. T8_ATTESTATION_FAILED:history.';
    END IF;

    IF NOT (
        (SELECT relation.relkind FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) = 'r'
        AND (SELECT relation.relnamespace FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) = target_namespace
        AND (SELECT relation.relowner FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) = owner_oid
        AND (SELECT relation.relpersistence FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) = 'p'
        AND NOT (SELECT relation.relrowsecurity FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid)
        AND NOT (SELECT relation.relforcerowsecurity FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid)
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_policy AS policy_definition
            WHERE policy_definition.polrelid = history_oid)
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_rewrite AS rule_definition
            WHERE rule_definition.ev_class = history_oid
              AND rule_definition.rulename <> '_RETURN')
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_trigger AS trigger_definition
            WHERE trigger_definition.tgrelid = history_oid
              AND NOT trigger_definition.tgisinternal)
        AND (SELECT count(*) FROM pg_catalog.pg_attribute AS attribute
             WHERE attribute.attrelid = history_oid
               AND attribute.attnum > 0
               AND NOT attribute.attisdropped) = 2
        AND EXISTS (
            SELECT 1
            FROM pg_catalog.pg_attribute AS attribute
            WHERE attribute.attrelid = history_oid
              AND attribute.attnum = 1
              AND attribute.attname = 'MigrationId'
              AND attribute.atttypid = 'pg_catalog.varchar'::regtype
              AND attribute.atttypmod = 154
              AND attribute.attnotnull
              AND NOT attribute.atthasdef
              AND attribute.attidentity = ''
              AND attribute.attgenerated = '')
        AND EXISTS (
            SELECT 1
            FROM pg_catalog.pg_attribute AS attribute
            WHERE attribute.attrelid = history_oid
              AND attribute.attnum = 2
              AND attribute.attname = 'ProductVersion'
              AND attribute.atttypid = 'pg_catalog.varchar'::regtype
              AND attribute.atttypmod = 36
              AND attribute.attnotnull
              AND NOT attribute.atthasdef
              AND attribute.attidentity = ''
              AND attribute.attgenerated = '')
        AND (SELECT count(*) FROM pg_catalog.pg_constraint AS constraint_definition
             WHERE constraint_definition.conrelid = history_oid) = 3
        AND EXISTS (
            SELECT 1
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.conrelid = history_oid
              AND constraint_definition.contype = 'p'
              AND constraint_definition.conname = 'PK___EFMigrationsHistory'
              AND constraint_definition.conkey = ARRAY[1]::smallint[]
              AND constraint_definition.convalidated
              AND constraint_definition.conenforced)
    )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: history-structure-mismatch. T8_ATTESTATION_FAILED:history.';
    END IF;

    EXECUTE format(
        'SELECT count(*), count(*) FILTER (WHERE "MigrationId" NOT IN (%L, %L)), COALESCE(bool_or("MigrationId" = %L), false), COALESCE(bool_or("MigrationId" = %L), false) FROM %I.%I',
        '20260815075156_InitialPostgresSchema',
        '20260815102612_T8CorrectivePostgresHardening',
        '20260815075156_InitialPostgresSchema',
        '20260815102612_T8CorrectivePostgresHardening',
        target_schema,
        '__EFMigrationsHistory')
    INTO history_count, unknown_history_count, initial_applied, corrective_applied;

    IF history_count NOT IN (1, 2) OR NOT initial_applied
        OR unknown_history_count <> 0
        OR (require_corrective AND NOT corrective_applied)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: unknown-history. T8_ATTESTATION_FAILED:history.';
    END IF;

    SELECT relation.oid
    INTO signups_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = 'signups'
      AND relation.relkind = 'r';

    SELECT relation.oid
    INTO index_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = 'ux_signups_waitlisted_order_per_help_need';

    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
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
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=identity.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_index AS index_definition
            WHERE index_definition.indexrelid = index_oid
              AND index_definition.indisunique
              AND index_definition.indisvalid
              AND index_definition.indisready
              AND index_definition.indislive)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=state.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
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
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=flags.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_index AS index_definition
            WHERE index_definition.indexrelid = index_oid
              AND index_definition.indnkeyatts = 3
              AND index_definition.indnatts = 3
              AND index_definition.indexprs IS NULL)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=key-count.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
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
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=key-order.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_index AS index_definition
            WHERE index_definition.indexrelid = index_oid
              AND index_definition.indcollation::text = '0 0 0'
              AND index_definition.indoption::text = '0 0 0')
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=key-options.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_class AS index_relation
            WHERE index_relation.oid = index_oid
              AND COALESCE(cardinality(index_relation.reloptions), 0) = 0)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=relation-options.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT allow_unvalidated
        AND index_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_index AS index_definition
            WHERE index_definition.indexrelid = index_oid
              AND pg_catalog.pg_get_expr(index_definition.indpred, signups_oid) = '(status = 2)')
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=predicate.; T8_ATTESTATION_FAILED:object.';
    END IF;

    IF NOT allow_unvalidated THEN
    IF index_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_index AS index_definition
            JOIN pg_catalog.pg_class AS index_relation
              ON index_relation.oid = index_definition.indexrelid
            JOIN pg_catalog.pg_am AS access_method
              ON access_method.oid = index_relation.relam
            WHERE index_definition.indexrelid = index_oid
              AND index_relation.relkind = 'i'
              AND index_relation.relowner = owner_oid
              AND index_definition.indrelid = signups_oid
              AND access_method.amname = 'btree'
              AND index_definition.indisunique
              AND (allow_unvalidated OR index_definition.indisvalid)
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
              AND pg_catalog.pg_get_expr(index_definition.indpred, signups_oid) = '(status = 2)'
        )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ux_signups_waitlisted_order_per_help_need; category=state.; T8_ATTESTATION_FAILED:object.';
    END IF;
    END IF;

    SELECT constraint_definition.oid
    INTO constraint_oid
    FROM pg_catalog.pg_constraint AS constraint_definition
    WHERE constraint_definition.conrelid = signups_oid
      AND constraint_definition.conname = 'ck_signups_transition_chronology';

    IF NOT allow_unvalidated
        AND constraint_oid IS NOT NULL
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
            MESSAGE = 'T8 object definition mismatch: object=ck_signups_transition_chronology; category=type.; T8_ATTESTATION_FAILED:object.';
    END IF;

    IF NOT allow_unvalidated
        AND constraint_oid IS NOT NULL
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
              AND constraint_definition.convalidated
              AND pg_catalog.pg_get_expr(
                  constraint_definition.conbin,
                  constraint_definition.conrelid) =
                      '((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))'
        )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ck_signups_transition_chronology; category=expression.; T8_ATTESTATION_FAILED:object.';
    END IF;

    IF require_corrective
        AND (index_oid IS NULL OR constraint_oid IS NULL)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: corrective-history-object-mismatch. T8_ATTESTATION_FAILED:history.';
    END IF;

    IF require_corrective
        AND EXISTS (
            SELECT 1
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.oid = constraint_oid
              AND NOT constraint_definition.convalidated)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: corrective-history-validation-mismatch. T8_ATTESTATION_FAILED:history.';
    END IF;

    IF NOT allow_unvalidated THEN
    FOREACH privilege_name IN ARRAY ARRAY[
        'SELECT', 'INSERT', 'UPDATE', 'DELETE',
        'TRUNCATE', 'REFERENCES', 'TRIGGER', 'MAINTAIN']
    LOOP
        IF pg_catalog.has_table_privilege('tabruk_app', history_oid, privilege_name) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001',
                MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
        END IF;
    END LOOP;
    FOREACH privilege_name IN ARRAY ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES']
    LOOP
        IF pg_catalog.has_any_column_privilege('tabruk_app', history_oid, privilege_name) THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001',
                MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
        END IF;
    END LOOP;
    FOREACH relation_name IN ARRAY ARRAY[
        'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
        'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
        'organizations', 'memberships', 'invitations', 'role_assignments',
        'role_change_requests', 'service_dates', 'help_needs', 'signups',
        'signup_member_participants', 'date_threads', 'thread_messages',
        'message_reports', 'thread_moderation_events', 'notifications',
        'device_registrations', 'outbox_messages', 'idempotency_records'
    ]
    LOOP
        SELECT relation.oid INTO relation_oid
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = relation_name;
        FOREACH privilege_name IN ARRAY ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE']
        LOOP
            IF NOT pg_catalog.has_table_privilege('tabruk_app', relation_oid, privilege_name)
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
            END IF;
        END LOOP;
        FOREACH privilege_name IN ARRAY ARRAY['TRUNCATE', 'REFERENCES', 'TRIGGER', 'MAINTAIN']
        LOOP
            IF pg_catalog.has_table_privilege('tabruk_app', relation_oid, privilege_name)
            THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
            END IF;
        END LOOP;
        IF pg_catalog.has_any_column_privilege('tabruk_app', relation_oid, 'REFERENCES')
        THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001',
                MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
        END IF;
    END LOOP;

    FOREACH relation_name IN ARRAY ARRAY['audit_events', 'privileged_access_events']
    LOOP
        SELECT relation.oid INTO relation_oid
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = relation_name;
        IF NOT pg_catalog.has_table_privilege('tabruk_app', relation_oid, 'INSERT') THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001',
                MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
        END IF;
        FOREACH privilege_name IN ARRAY ARRAY[
            'SELECT', 'UPDATE', 'DELETE', 'TRUNCATE',
            'REFERENCES', 'TRIGGER', 'MAINTAIN']
        LOOP
            IF pg_catalog.has_table_privilege('tabruk_app', relation_oid, privilege_name) THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
            END IF;
        END LOOP;
        FOREACH privilege_name IN ARRAY ARRAY['SELECT', 'UPDATE', 'REFERENCES']
        LOOP
            IF pg_catalog.has_any_column_privilege('tabruk_app', relation_oid, privilege_name) THEN
                RAISE EXCEPTION USING ERRCODE = 'P0001',
                    MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
            END IF;
        END LOOP;
    END LOOP;

    FOREACH relation_name IN ARRAY ARRAY['identity_role_claims_Id_seq', 'identity_user_claims_Id_seq']
    LOOP
        SELECT relation.oid INTO relation_oid
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = relation_name;
        IF NOT pg_catalog.has_sequence_privilege('tabruk_app', relation_oid, 'USAGE')
            OR NOT pg_catalog.has_sequence_privilege('tabruk_app', relation_oid, 'SELECT')
            OR pg_catalog.has_sequence_privilege('tabruk_app', relation_oid, 'UPDATE')
        THEN
            RAISE EXCEPTION USING ERRCODE = 'P0001',
                MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
        END IF;
    END LOOP;

    IF EXISTS (
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
          AND (
              privilege.grantee = 0
              OR (
                  privilege.is_grantable
                  AND privilege.grantee <> 0
                  AND (
                      privilege.grantee = app_oid
                      OR pg_catalog.pg_has_role('tabruk_app', privilege.grantee, 'MEMBER')
                  )
              )
          )
    )
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_attribute AS attribute
              ON attribute.attrelid = relation.oid
            CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS privilege
            WHERE relation.relnamespace = target_namespace
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped
              AND (
                  privilege.grantee = 0
                  OR privilege.grantee = app_oid
                  OR pg_catalog.pg_has_role('tabruk_app', privilege.grantee, 'MEMBER')
              )
        )
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: privilege-mismatch. T8_ATTESTATION_FAILED:acl.';
    END IF;
    END IF;
END
$body$;

\echo T8_STAGE=preflight
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
            'tabruk_app');

        FOR inherited_role IN
            SELECT role_definition.rolname
            FROM pg_catalog.pg_roles AS role_definition
            WHERE role_definition.rolname NOT IN (current_user, 'tabruk_app')
              AND pg_catalog.pg_has_role(
                  'tabruk_app',
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
DO $tabruk$
DECLARE
    target_namespace oid;
BEGIN
    target_namespace := pg_temp.t8_target_namespace();
    IF (
        SELECT count(*)
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = target_namespace
          AND relation.relname = ANY(ARRAY[
              'users', 'identity_roles', 'identity_user_claims', 'identity_role_claims',
              'identity_user_logins', 'identity_user_roles', 'identity_user_tokens',
              'organizations', 'memberships', 'invitations', 'role_assignments',
              'role_change_requests', 'service_dates', 'help_needs', 'signups',
              'signup_member_participants', 'date_threads', 'thread_messages',
              'message_reports', 'thread_moderation_events', 'notifications',
              'device_registrations', 'outbox_messages', 'idempotency_records',
              'audit_events', 'privileged_access_events', '__EFMigrationsHistory'])
          AND relation.relkind = 'r'
    ) <> 27
        OR (
            SELECT count(*)
            FROM pg_catalog.pg_class AS relation
            WHERE relation.relnamespace = target_namespace
              AND relation.relname = ANY(ARRAY[
                  'identity_role_claims_Id_seq', 'identity_user_claims_Id_seq'])
              AND relation.relkind = 'S'
        ) <> 2
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: partial-initial-inventory. T8_ATTESTATION_FAILED:history.';
    END IF;
END
$tabruk$;
SELECT pg_temp.t8_target_namespace();
BEGIN;
LOCK TABLE
    :"target_schema"."users",
    :"target_schema"."identity_roles",
    :"target_schema"."identity_user_claims",
    :"target_schema"."identity_role_claims",
    :"target_schema"."identity_user_logins",
    :"target_schema"."identity_user_roles",
    :"target_schema"."identity_user_tokens",
    :"target_schema"."organizations",
    :"target_schema"."memberships",
    :"target_schema"."invitations",
    :"target_schema"."role_assignments",
    :"target_schema"."role_change_requests",
    :"target_schema"."service_dates",
    :"target_schema"."help_needs",
    :"target_schema"."signups",
    :"target_schema"."signup_member_participants",
    :"target_schema"."date_threads",
    :"target_schema"."thread_messages",
    :"target_schema"."message_reports",
    :"target_schema"."thread_moderation_events",
    :"target_schema"."notifications",
    :"target_schema"."device_registrations",
    :"target_schema"."outbox_messages",
    :"target_schema"."idempotency_records",
    :"target_schema"."audit_events",
    :"target_schema"."privileged_access_events",
    :"target_schema"."__EFMigrationsHistory"
    IN ACCESS EXCLUSIVE MODE;
SELECT pg_temp.t8_attest(false, true);
CREATE TEMP TABLE IF NOT EXISTS pg_temp.t8_entry_state
(
    index_present boolean NOT NULL,
    entry_index_oid oid,
    constraint_present boolean NOT NULL,
    constraint_validated boolean NOT NULL,
    created_index_oid oid,
    created_constraint_oid oid,
    index_created boolean NOT NULL DEFAULT false
) ON COMMIT PRESERVE ROWS;
TRUNCATE pg_temp.t8_entry_state;
INSERT INTO pg_temp.t8_entry_state
    (entry_index_oid, index_present, constraint_present, constraint_validated)
SELECT
    (
        SELECT relation.oid
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = pg_temp.t8_target_namespace()
          AND relation.relname = 'ux_signups_waitlisted_order_per_help_need'),
    EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = pg_temp.t8_target_namespace()
          AND relation.relname = 'ux_signups_waitlisted_order_per_help_need'),
    EXISTS (
        SELECT 1
        FROM pg_catalog.pg_constraint AS constraint_definition
        JOIN pg_catalog.pg_class AS relation
          ON relation.oid = constraint_definition.conrelid
        WHERE relation.relnamespace = pg_temp.t8_target_namespace()
          AND relation.relname = 'signups'
          AND constraint_definition.conname = 'ck_signups_transition_chronology'),
    COALESCE((
        SELECT constraint_definition.convalidated
        FROM pg_catalog.pg_constraint AS constraint_definition
        JOIN pg_catalog.pg_class AS relation
          ON relation.oid = constraint_definition.conrelid
        WHERE relation.relnamespace = pg_temp.t8_target_namespace()
          AND relation.relname = 'signups'
          AND constraint_definition.conname = 'ck_signups_transition_chronology'), false);
UPDATE pg_temp.t8_entry_state
SET created_index_oid = NULL,
    created_constraint_oid = NULL,
    index_created = false;
SELECT index_present AS t8_index_present
FROM pg_temp.t8_entry_state
\gset
CREATE OR REPLACE FUNCTION pg_temp.t8_verify_created_index()
RETURNS void
LANGUAGE plpgsql
AS $body$
DECLARE
    target_schema text := current_setting('tabruk.target_schema', true);
    target_namespace oid;
    owner_oid oid;
    signups_oid oid;
    index_oid oid;
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_temp.t8_entry_state
        WHERE created_index_oid IS NOT NULL)
    THEN
        RETURN;
    END IF;

    target_namespace := pg_temp.t8_target_namespace();
    SELECT session_state.owner_oid
    INTO owner_oid
    FROM pg_temp.t8_session_state AS session_state
    WHERE session_state.singleton;
    SELECT relation.oid
    INTO signups_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = 'signups'
      AND relation.relkind = 'r';
    SELECT relation.oid
    INTO index_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = 'ux_signups_waitlisted_order_per_help_need';

    IF index_oid IS DISTINCT FROM (
        SELECT created_index_oid
        FROM pg_temp.t8_entry_state)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_index AS index_definition
        JOIN pg_catalog.pg_class AS index_relation
          ON index_relation.oid = index_definition.indexrelid
        JOIN pg_catalog.pg_am AS access_method
          ON access_method.oid = index_relation.relam
        WHERE index_definition.indexrelid = index_oid
          AND index_relation.relnamespace = target_namespace
          AND index_relation.relkind = 'i'
          AND index_relation.relowner = owner_oid
          AND index_definition.indrelid = signups_oid
          AND access_method.amname = 'btree'
          AND index_definition.indisunique
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
          AND pg_catalog.pg_get_expr(index_definition.indpred, signups_oid) = '(status = 2)')
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
    END IF;
END
$body$;

CREATE OR REPLACE FUNCTION pg_temp.t8_drop_created_index()
RETURNS void
LANGUAGE plpgsql
AS $body$
DECLARE
    target_schema text := current_setting('tabruk.target_schema', true);
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_temp.t8_entry_state
        WHERE created_index_oid IS NOT NULL)
    THEN
        RETURN;
    END IF;

    PERFORM pg_temp.t8_verify_created_index();
    IF current_setting('tabruk.t8_test_pause_after_compensation_verify', true) = 'on' THEN
        PERFORM pg_catalog.pg_advisory_lock(84150815102617);
        PERFORM pg_catalog.pg_advisory_unlock(84150815102617);
    END IF;
    EXECUTE format(
        'DROP INDEX %I.%I',
        target_schema,
        'ux_signups_waitlisted_order_per_help_need');
END
$body$;

CREATE OR REPLACE FUNCTION pg_temp.t8_drop_created_constraint()
RETURNS void
LANGUAGE plpgsql
AS $body$
DECLARE
    target_schema text := current_setting('tabruk.target_schema', true);
    target_namespace oid;
    signups_oid oid;
    constraint_oid oid;
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_temp.t8_entry_state
        WHERE created_constraint_oid IS NOT NULL)
    THEN
        RETURN;
    END IF;

    target_namespace := pg_temp.t8_target_namespace();
    SELECT relation.oid
    INTO signups_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = 'signups'
      AND relation.relkind = 'r';
    SELECT constraint_definition.oid
    INTO constraint_oid
    FROM pg_catalog.pg_constraint AS constraint_definition
    WHERE constraint_definition.conrelid = signups_oid
      AND constraint_definition.conname = 'ck_signups_transition_chronology';

    IF constraint_oid IS DISTINCT FROM (
        SELECT created_constraint_oid
        FROM pg_temp.t8_entry_state)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
    END IF;

    IF NOT EXISTS (
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
                  '((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))')
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
    END IF;

    EXECUTE format(
        'ALTER TABLE %I.%I DROP CONSTRAINT %I',
        target_schema,
        'signups',
        'ck_signups_transition_chronology');
END
$body$;
REVOKE ALL PRIVILEGES ON TABLE
    :"target_schema"."users", :"target_schema"."identity_roles",
    :"target_schema"."identity_user_claims", :"target_schema"."identity_role_claims",
    :"target_schema"."identity_user_logins", :"target_schema"."identity_user_roles",
    :"target_schema"."identity_user_tokens", :"target_schema"."organizations",
    :"target_schema"."memberships", :"target_schema"."invitations",
    :"target_schema"."role_assignments", :"target_schema"."role_change_requests",
    :"target_schema"."service_dates", :"target_schema"."help_needs", :"target_schema"."signups",
    :"target_schema"."signup_member_participants", :"target_schema"."date_threads",
    :"target_schema"."thread_messages", :"target_schema"."message_reports",
    :"target_schema"."thread_moderation_events", :"target_schema"."notifications",
    :"target_schema"."device_registrations", :"target_schema"."outbox_messages",
    :"target_schema"."idempotency_records", :"target_schema"."audit_events",
    :"target_schema"."privileged_access_events", :"target_schema"."__EFMigrationsHistory"
    FROM "tabruk_app", PUBLIC;
REVOKE ALL PRIVILEGES ON SEQUENCE
    :"target_schema"."identity_role_claims_Id_seq",
    :"target_schema"."identity_user_claims_Id_seq"
    FROM "tabruk_app", PUBLIC;
REVOKE ALL PRIVILEGES ON TABLE
    :"target_schema"."__EFMigrationsHistory"
    FROM "tabruk_app", PUBLIC;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
    :"target_schema"."users", :"target_schema"."identity_roles",
    :"target_schema"."identity_user_claims", :"target_schema"."identity_role_claims",
    :"target_schema"."identity_user_logins", :"target_schema"."identity_user_roles",
    :"target_schema"."identity_user_tokens", :"target_schema"."organizations",
    :"target_schema"."memberships", :"target_schema"."invitations",
    :"target_schema"."role_assignments", :"target_schema"."role_change_requests",
    :"target_schema"."service_dates", :"target_schema"."help_needs", :"target_schema"."signups",
    :"target_schema"."signup_member_participants", :"target_schema"."date_threads",
    :"target_schema"."thread_messages", :"target_schema"."message_reports",
    :"target_schema"."thread_moderation_events", :"target_schema"."notifications",
    :"target_schema"."device_registrations", :"target_schema"."outbox_messages",
    :"target_schema"."idempotency_records"
    TO "tabruk_app";
GRANT INSERT ON TABLE
    :"target_schema"."audit_events",
    :"target_schema"."privileged_access_events"
    TO "tabruk_app";
GRANT USAGE, SELECT ON SEQUENCE
    :"target_schema"."identity_role_claims_Id_seq",
    :"target_schema"."identity_user_claims_Id_seq"
    TO "tabruk_app";
SELECT pg_temp.t8_attest(false, true);
COMMIT;

\echo T8_STAGE=chronology
SELECT pg_temp.t8_target_namespace();
BEGIN;
LOCK TABLE
    :"target_schema"."users", :"target_schema"."identity_roles",
    :"target_schema"."identity_user_claims", :"target_schema"."identity_role_claims",
    :"target_schema"."identity_user_logins", :"target_schema"."identity_user_roles",
    :"target_schema"."identity_user_tokens", :"target_schema"."organizations",
    :"target_schema"."memberships", :"target_schema"."invitations",
    :"target_schema"."role_assignments", :"target_schema"."role_change_requests",
    :"target_schema"."service_dates", :"target_schema"."help_needs", :"target_schema"."signups",
    :"target_schema"."signup_member_participants", :"target_schema"."date_threads",
    :"target_schema"."thread_messages", :"target_schema"."message_reports",
    :"target_schema"."thread_moderation_events", :"target_schema"."notifications",
    :"target_schema"."device_registrations", :"target_schema"."outbox_messages",
    :"target_schema"."idempotency_records", :"target_schema"."audit_events",
    :"target_schema"."privileged_access_events", :"target_schema"."__EFMigrationsHistory"
    IN ACCESS EXCLUSIVE MODE;
SELECT pg_temp.t8_attest(false, true);
DO $tabruk$
DECLARE
    target_schema text := current_setting('tabruk.target_schema', true);
    signups_oid oid;
    constraint_oid oid;
BEGIN
    SELECT relation.oid
    INTO signups_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = pg_temp.t8_target_namespace()
      AND relation.relname = 'signups'
      AND relation.relkind = 'r';
    SELECT constraint_definition.oid
    INTO constraint_oid
    FROM pg_catalog.pg_constraint AS constraint_definition
    WHERE constraint_definition.conrelid = signups_oid
      AND constraint_definition.conname = 'ck_signups_transition_chronology';
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
            MESSAGE = 'T8 object definition mismatch: object=ck_signups_transition_chronology; category=type.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF constraint_oid IS NOT NULL
        AND NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.oid = constraint_oid
              AND pg_catalog.pg_get_expr(
                  constraint_definition.conbin,
                  constraint_definition.conrelid) =
                      '((last_transition_at IS NULL) OR (last_transition_at >= submitted_at))')
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 object definition mismatch: object=ck_signups_transition_chronology; category=expression.; T8_ATTESTATION_FAILED:object.';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM pg_catalog.pg_constraint AS constraint_definition
        JOIN pg_catalog.pg_class AS relation
          ON relation.oid = constraint_definition.conrelid
        WHERE relation.relnamespace = pg_temp.t8_target_namespace()
          AND relation.relname = 'signups'
          AND constraint_definition.conname = 'ck_signups_transition_chronology')
    THEN
        EXECUTE format(
            'ALTER TABLE %I.%I ADD CONSTRAINT %I CHECK ("last_transition_at" IS NULL OR "last_transition_at" >= "submitted_at") NOT VALID',
            target_schema,
            'signups',
            'ck_signups_transition_chronology');
        SELECT constraint_definition.oid
        INTO STRICT constraint_oid
        FROM pg_catalog.pg_constraint AS constraint_definition
        WHERE constraint_definition.conrelid = signups_oid
          AND constraint_definition.conname = 'ck_signups_transition_chronology';
        UPDATE pg_temp.t8_entry_state
        SET created_constraint_oid = constraint_oid;
    END IF;
END
$tabruk$;
COMMIT;
DO $pause$
BEGIN
    IF current_setting('tabruk.t8_test_pause_after_constraint_capture', true) = 'on' THEN
        PERFORM pg_catalog.pg_advisory_lock(84150815102618);
        PERFORM pg_catalog.pg_advisory_unlock(84150815102618);
    END IF;
END
$pause$;

\echo T8_STAGE=chronology
SELECT pg_temp.t8_target_namespace();
\set ON_ERROR_STOP off
BEGIN;
LOCK TABLE
    :"target_schema"."users", :"target_schema"."identity_roles",
    :"target_schema"."identity_user_claims", :"target_schema"."identity_role_claims",
    :"target_schema"."identity_user_logins", :"target_schema"."identity_user_roles",
    :"target_schema"."identity_user_tokens", :"target_schema"."organizations",
    :"target_schema"."memberships", :"target_schema"."invitations",
    :"target_schema"."role_assignments", :"target_schema"."role_change_requests",
    :"target_schema"."service_dates", :"target_schema"."help_needs", :"target_schema"."signups",
    :"target_schema"."signup_member_participants", :"target_schema"."date_threads",
    :"target_schema"."thread_messages", :"target_schema"."message_reports",
    :"target_schema"."thread_moderation_events", :"target_schema"."notifications",
    :"target_schema"."device_registrations", :"target_schema"."outbox_messages",
    :"target_schema"."idempotency_records", :"target_schema"."audit_events",
    :"target_schema"."privileged_access_events", :"target_schema"."__EFMigrationsHistory"
    IN ACCESS EXCLUSIVE MODE;
SELECT pg_temp.t8_attest(false, true);
ALTER TABLE :"target_schema"."signups"
    VALIDATE CONSTRAINT "ck_signups_transition_chronology";
\if :ERROR
    ROLLBACK;
    \set ON_ERROR_STOP on
    \echo T8_STAGE=compensation
    \set ON_ERROR_STOP off
    BEGIN;
    SELECT pg_temp.t8_target_namespace();
    LOCK TABLE :"target_schema"."signups" IN ACCESS EXCLUSIVE MODE;
    SELECT pg_temp.t8_drop_created_constraint();
    \if :ERROR
        ROLLBACK;
        \set ON_ERROR_STOP on
        \echo T8_RESULT=compensation_failed
        DO $fail$
        BEGIN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
        END
        $fail$;
    \endif
    COMMIT;
    \set ON_ERROR_STOP on
    \echo T8_RESULT=failed
    DO $fail$
    BEGIN
        RAISE EXCEPTION USING ERRCODE = '23514', MESSAGE = 'T8 chronology validation failed';
    END
    $fail$;
\endif
COMMIT;
\if :ERROR
    ROLLBACK;
    \set ON_ERROR_STOP on
    \echo T8_RESULT=failed
    DO $fail$
    BEGIN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8 chronology validation failed';
    END
    $fail$;
\endif
\set ON_ERROR_STOP on

\echo T8_STAGE=index
SELECT pg_temp.t8_target_namespace();
\set t8_index_failed false
\set t8_index_duplicate false
\set ON_ERROR_STOP off
\if :t8_index_present
\else
DO $tabruk$
DECLARE
    duplicate_waitlist boolean;
BEGIN
    EXECUTE format(
        'SELECT EXISTS (
             SELECT 1
             FROM %I.%I
             WHERE "status" = 2
             GROUP BY "organization_id", "help_need_id", "waitlist_order"
             HAVING count(*) > 1)',
        current_setting('tabruk.target_schema', true),
        'signups')
    INTO duplicate_waitlist;
    IF duplicate_waitlist THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            CONSTRAINT = 'ux_signups_waitlisted_order_per_help_need',
            MESSAGE = 'Duplicate waitlist positions prevent the corrective waitlist index from being created.';
    END IF;
END
$tabruk$;
\if :ERROR
    \set t8_index_failed true
    \set t8_index_duplicate true
\else
    CREATE UNIQUE INDEX CONCURRENTLY
        "ux_signups_waitlisted_order_per_help_need"
        ON :"target_schema"."signups"
            ("organization_id", "help_need_id", "waitlist_order")
        WHERE "status" = 2;
    \if :ERROR
        \set t8_index_failed true
    \else
        UPDATE pg_temp.t8_entry_state AS entry_state
        SET created_index_oid = relation.oid,
            index_created = true
        FROM pg_catalog.pg_class AS relation
        WHERE relation.relnamespace = pg_temp.t8_target_namespace()
          AND relation.relname = 'ux_signups_waitlisted_order_per_help_need'
          AND entry_state.entry_index_oid IS NULL;
        DO $pause$
        BEGIN
            IF current_setting('tabruk.t8_test_pause_after_index_capture', true) = 'on' THEN
                PERFORM pg_catalog.pg_advisory_lock(84150815102616);
                PERFORM pg_catalog.pg_advisory_unlock(84150815102616);
            END IF;
        END
        $pause$;
    \endif
\endif
\endif
\if :t8_index_failed
    \set ON_ERROR_STOP on
    \echo T8_STAGE=compensation
    \set ON_ERROR_STOP off
    BEGIN;
    SELECT pg_temp.t8_target_namespace();
    LOCK TABLE :"target_schema"."signups" IN ACCESS EXCLUSIVE MODE;
    SELECT pg_temp.t8_drop_created_index();
    SELECT pg_temp.t8_drop_created_constraint();
    \if :ERROR
        ROLLBACK;
        \set ON_ERROR_STOP on
        \echo T8_RESULT=compensation_failed
        DO $fail$
        BEGIN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
        END
        $fail$;
    \endif
    COMMIT;
        \set ON_ERROR_STOP on
        \if :t8_index_duplicate
            \echo T8_RESULT=failed
            DO $fail$
            BEGIN
                RAISE EXCEPTION USING
                    ERRCODE = '23505',
                    CONSTRAINT = 'ux_signups_waitlisted_order_per_help_need',
                    MESSAGE = 'Duplicate waitlist positions prevent the corrective waitlist index from being created.';
            END
            $fail$;
        \else
            \echo T8_RESULT=failed
            DO $fail$
            BEGIN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8 index creation failed';
            END
            $fail$;
        \endif
\else
        \set ON_ERROR_STOP on
        SELECT pg_temp.t8_verify_created_index();
\endif
\set ON_ERROR_STOP on

\echo T8_STAGE=finalize
SELECT pg_temp.t8_target_namespace();
CREATE TEMP TABLE IF NOT EXISTS pg_temp.t8_finalize_failure
(
    sqlstate text NOT NULL,
    message text NOT NULL
) ON COMMIT PRESERVE ROWS;
TRUNCATE pg_temp.t8_finalize_failure;
\set ON_ERROR_STOP off
BEGIN;
LOCK TABLE
    :"target_schema"."users", :"target_schema"."identity_roles",
    :"target_schema"."identity_user_claims", :"target_schema"."identity_role_claims",
    :"target_schema"."identity_user_logins", :"target_schema"."identity_user_roles",
    :"target_schema"."identity_user_tokens", :"target_schema"."organizations",
    :"target_schema"."memberships", :"target_schema"."invitations",
    :"target_schema"."role_assignments", :"target_schema"."role_change_requests",
    :"target_schema"."service_dates", :"target_schema"."help_needs", :"target_schema"."signups",
    :"target_schema"."signup_member_participants", :"target_schema"."date_threads",
    :"target_schema"."thread_messages", :"target_schema"."message_reports",
    :"target_schema"."thread_moderation_events", :"target_schema"."notifications",
    :"target_schema"."device_registrations", :"target_schema"."outbox_messages",
    :"target_schema"."idempotency_records", :"target_schema"."audit_events",
    :"target_schema"."privileged_access_events", :"target_schema"."__EFMigrationsHistory"
    IN ACCESS EXCLUSIVE MODE;
DO $finalize$
DECLARE
    target_schema text := current_setting('tabruk.target_schema', true);
BEGIN
    PERFORM pg_temp.t8_attest(false, false);
    IF current_setting('tabruk.t8_force_final_attestation_failure', true) = 'on' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'T8 forced final attestation failure';
    END IF;
    IF current_setting('tabruk.t8_force_history_insert_failure', true) = 'on' THEN
        RAISE EXCEPTION USING
            ERRCODE = 'P0001',
            MESSAGE = 'T8 forced history insert failure';
    END IF;
    EXECUTE format(
        'INSERT INTO %I.%I ("MigrationId", "ProductVersion")
         SELECT %L, %L
         WHERE NOT EXISTS (
             SELECT 1
             FROM %I.%I
             WHERE "MigrationId" = %L)',
        target_schema,
        '__EFMigrationsHistory',
        '20260815102612_T8CorrectivePostgresHardening',
        '10.0.11',
        target_schema,
        '__EFMigrationsHistory',
        '20260815102612_T8CorrectivePostgresHardening');
EXCEPTION WHEN OTHERS THEN
    INSERT INTO pg_temp.t8_finalize_failure(sqlstate, message)
    VALUES (
        CASE WHEN SQLSTATE = 'XX000' THEN 'P0001' ELSE SQLSTATE END,
        CASE
            WHEN SQLSTATE = 'XX000'
            THEN 'T8 concurrent catalog mutation detected; T8_ATTESTATION_FAILED:concurrency.'
            ELSE SQLERRM
        END);
END
$finalize$;
SELECT EXISTS (SELECT 1 FROM pg_temp.t8_finalize_failure) AS t8_finalize_failed
\gset
\if :t8_finalize_failed
    SELECT sqlstate AS t8_failure_sqlstate, message AS t8_failure_message
    FROM pg_temp.t8_finalize_failure
    LIMIT 1
    \gset
    ROLLBACK;
    \set ON_ERROR_STOP on
    \echo T8_STAGE=compensation
    \set ON_ERROR_STOP off
    BEGIN;
    SELECT pg_temp.t8_target_namespace();
    LOCK TABLE :"target_schema"."signups" IN ACCESS EXCLUSIVE MODE;
    SELECT pg_temp.t8_drop_created_index();
    SELECT pg_temp.t8_drop_created_constraint();
    \if :ERROR
        ROLLBACK;
        \set ON_ERROR_STOP on
        \echo T8_RESULT=compensation_failed
        DO $fail$
        BEGIN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
        END
        $fail$;
    \endif
    COMMIT;
    \set ON_ERROR_STOP on
    \echo T8_RESULT=failed
    SELECT format(
        'DO $fail$ BEGIN RAISE EXCEPTION USING ERRCODE = %L, MESSAGE = %L; END $fail$;',
        :'t8_failure_sqlstate',
        :'t8_failure_message')
    \gexec
\else
    COMMIT;
    \if :ERROR
        ROLLBACK;
        \set ON_ERROR_STOP on
        \echo T8_STAGE=compensation
        \set ON_ERROR_STOP off
        BEGIN;
        SELECT pg_temp.t8_target_namespace();
        LOCK TABLE :"target_schema"."signups" IN ACCESS EXCLUSIVE MODE;
        SELECT pg_temp.t8_drop_created_index();
        SELECT pg_temp.t8_drop_created_constraint();
        \if :ERROR
            ROLLBACK;
            \set ON_ERROR_STOP on
            \echo T8_RESULT=compensation_failed
            DO $fail$
            BEGIN
                RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8_COMPENSATION_FAILED';
            END
            $fail$;
        \endif
        COMMIT;
        \set ON_ERROR_STOP on
        \echo T8_RESULT=failed
        DO $fail$
        BEGIN
            RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'T8 final history commit failed';
        END
        $fail$;
    \endif
    \set ON_ERROR_STOP on
    \echo T8_RESULT=success
\endif
