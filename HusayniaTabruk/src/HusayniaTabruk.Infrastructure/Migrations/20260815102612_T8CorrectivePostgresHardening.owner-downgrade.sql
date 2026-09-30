\set ON_ERROR_STOP on

-- Production T8 downgrade is a logical marker removal.  It never drops the safety objects.
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
BEGIN
    SELECT namespace_definition.oid
    INTO target_namespace
    FROM pg_catalog.pg_namespace AS namespace_definition
    WHERE namespace_definition.nspname = target_schema;
    IF target_schema IS NULL
        OR target_schema !~ '^[a-z_][a-z0-9_$]{0,62}$'
        OR target_schema IN ('pg_catalog', 'information_schema')
        OR target_schema LIKE 'pg\_%' ESCAPE '\'
        OR pg_catalog.current_setting('search_path') <> 'pg_catalog'
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

CREATE OR REPLACE FUNCTION pg_temp.t8_down_attest(require_history boolean)
RETURNS void
LANGUAGE plpgsql
AS $body$
DECLARE
    target_schema text := pg_catalog.current_setting('tabruk.target_schema', true);
    target_namespace oid;
    owner_oid oid;
    app_oid oid;
    history_oid oid;
    signups_oid oid;
    index_oid oid;
    constraint_oid oid;
    relation_oid oid;
    relation_name text;
    privilege_name text;
    history_count integer;
    unknown_history_count integer;
    initial_applied boolean;
    corrective_applied boolean;
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
              'audit_events', 'privileged_access_events', '__EFMigrationsHistory'])
          AND (
              relation.relkind <> 'r'
              OR relation.relowner <> owner_oid
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

    SELECT relation.oid
    INTO history_oid
    FROM pg_catalog.pg_class AS relation
    WHERE relation.relnamespace = target_namespace
      AND relation.relname = '__EFMigrationsHistory';
    IF history_oid IS NULL
        OR (SELECT relation.relkind FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) <> 'r'
        OR (SELECT relation.relnamespace FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) <> target_namespace
        OR (SELECT relation.relowner FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) <> owner_oid
        OR (SELECT relation.relpersistence FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid) <> 'p'
        OR (SELECT relation.relrowsecurity FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid)
        OR (SELECT relation.relforcerowsecurity FROM pg_catalog.pg_class AS relation WHERE relation.oid = history_oid)
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.pg_policy AS policy_definition
            WHERE policy_definition.polrelid = history_oid)
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.pg_rewrite AS rule_definition
            WHERE rule_definition.ev_class = history_oid
              AND rule_definition.rulename <> '_RETURN')
        OR EXISTS (
            SELECT 1
            FROM pg_catalog.pg_trigger AS trigger_definition
            WHERE trigger_definition.tgrelid = history_oid
              AND NOT trigger_definition.tgisinternal)
        OR (SELECT count(*) FROM pg_catalog.pg_attribute AS attribute
            WHERE attribute.attrelid = history_oid
              AND attribute.attnum > 0
              AND NOT attribute.attisdropped) <> 2
        OR NOT EXISTS (
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
        OR NOT EXISTS (
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
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.conrelid = history_oid
              AND constraint_definition.contype = 'p'
              AND constraint_definition.conname = 'PK___EFMigrationsHistory'
              AND constraint_definition.conkey = ARRAY[1]::smallint[]
              AND constraint_definition.convalidated
              AND constraint_definition.conenforced)
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
    IF history_count NOT IN (1, 2)
        OR unknown_history_count <> 0
        OR NOT initial_applied
        OR (require_history AND NOT corrective_applied)
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001',
            MESSAGE = 'T8 pre-history attestation failed: history-mismatch. T8_ATTESTATION_FAILED:history.';
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

    IF index_oid IS NULL
        OR NOT EXISTS (
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
                  FROM pg_catalog.pg_constraint AS index_constraint
                  WHERE index_constraint.conindid = index_oid)
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
                  SELECT array_agg(operator_class.opcname::text ORDER BY operator_classes.ordinality)
                  FROM unnest(index_definition.indclass)
                      WITH ORDINALITY AS operator_classes(operator_class_oid, ordinality)
                  JOIN pg_catalog.pg_opclass AS operator_class
                    ON operator_class.oid = operator_classes.operator_class_oid
                  JOIN pg_catalog.pg_namespace AS operator_namespace
                    ON operator_namespace.oid = operator_class.opcnamespace
                  WHERE operator_namespace.nspname = 'pg_catalog'
                    AND operator_class.opcmethod = index_relation.relam
              ) = ARRAY['uuid_ops', 'uuid_ops', 'int8_ops']
              AND pg_catalog.pg_get_expr(index_definition.indpred, signups_oid) = '(status = 2)'
        )
        OR constraint_oid IS NULL
        OR NOT EXISTS (
            SELECT 1
            FROM pg_catalog.pg_constraint AS constraint_definition
            WHERE constraint_definition.oid = constraint_oid
              AND constraint_definition.contype = 'c'
              AND constraint_definition.convalidated
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
            MESSAGE = 'T8 pre-history attestation failed: corrective-history-object-mismatch. T8_ATTESTATION_FAILED:object.';
    END IF;

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
END
$body$;

\echo T8_STAGE=down
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
SELECT pg_temp.t8_down_attest(false);
DELETE FROM :"target_schema"."__EFMigrationsHistory"
WHERE "MigrationId" = '20260815102612_T8CorrectivePostgresHardening';
SELECT pg_temp.t8_down_attest(false);
COMMIT;
\echo T8_STAGE=down
\echo T8_RESULT=success
