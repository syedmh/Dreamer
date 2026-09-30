# T18 schema handoff

This artifact copies the landed T04 schema contract. The source of truth is
`src/Husaynia.Infrastructure/Identity/IdentityAuditDatabaseInvariant.cs`; T18 must not rename or
independently recreate any object or SQL below.

## Ownership and sequencing

- T04 owns `IdentityAuditDatabaseInvariant.InstallSql`, `DownSql`, the
  `IdentityAnonymousRateLimit` entity/configuration, and integration-fixture coverage.
- **T18 exclusively owns authoring and applying the EF migration and model snapshot changes.**
- Migration `Up` must execute the complete `InstallSql` below.
- Migration `Down` must execute the complete `DownSql` below.
- Application startup must not execute either SQL constant and must not require schema-alteration
  permission.
- Deploy the schema before deploying the runtime. The forward change is additive and compatible
  with the old runtime; the new runtime fails closed when the limiter table is absent.
- Roll back the runtime before executing migration `Down`. `DownSql` removes the two triggers and
  ephemeral limiter state, but it does not remove account, bootstrap-seal, or audit rows.

## Exact owned schema

| Kind | Exact name |
|---|---|
| Rate-limit table | `[dbo].[IdentityAnonymousRateLimits]` |
| Primary key | `PK_IdentityAnonymousRateLimits` |
| Unique partition constraint | `UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint` |
| Retention/expiry index | `IX_IdentityAnonymousRateLimits_RetainUntilUtc` |
| Positive-count check | `CK_IdentityAnonymousRateLimits_RequestCount` |
| Window-order check | `CK_IdentityAnonymousRateLimits_Window` |
| Retention check | `CK_IdentityAnonymousRateLimits_Retention` |
| Audit append-only trigger | `[dbo].[TR_IdentityAuditEvents_AppendOnly]` |
| Permanent-seal trigger | `[dbo].[TR_IdentityBootstrapState_PermanentSeal]` |

The unique constraint is the lookup structure for the exact
`EndpointFamily`/`ClientFingerprint` partition. Do not add a differently named lookup index.

| Column | Exact SQL definition |
|---|---|
| `Id` | `bigint IDENTITY(1,1) NOT NULL` |
| `EndpointFamily` | `nvarchar(32) NOT NULL` |
| `ClientFingerprint` | `binary(32) NOT NULL` |
| `WindowStartedAtUtc` | `datetimeoffset(7) NOT NULL` |
| `WindowEndsAtUtc` | `datetimeoffset(7) NOT NULL` |
| `RequestCount` | `int NOT NULL` |
| `RetainUntilUtc` | `datetimeoffset(7) NOT NULL` |

Exact constraint semantics:

- `PRIMARY KEY CLUSTERED ([Id])`
- `UNIQUE ([EndpointFamily], [ClientFingerprint])`
- `CHECK ([RequestCount] > 0)`
- `CHECK ([WindowEndsAtUtc] > [WindowStartedAtUtc])`
- `CHECK ([RetainUntilUtc] >= [WindowEndsAtUtc])`

## Exact `InstallSql`

```sql
SET XACT_ABORT ON;

IF OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[IdentityAnonymousRateLimits]
    (
        [Id] bigint IDENTITY(1,1) NOT NULL,
        [EndpointFamily] nvarchar(32) NOT NULL,
        [ClientFingerprint] binary(32) NOT NULL,
        [WindowStartedAtUtc] datetimeoffset(7) NOT NULL,
        [WindowEndsAtUtc] datetimeoffset(7) NOT NULL,
        [RequestCount] int NOT NULL,
        [RetainUntilUtc] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_IdentityAnonymousRateLimits]
            PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [UQ_IdentityAnonymousRateLimits_EndpointFamily_ClientFingerprint]
            UNIQUE ([EndpointFamily], [ClientFingerprint]),
        CONSTRAINT [CK_IdentityAnonymousRateLimits_RequestCount]
            CHECK ([RequestCount] > 0),
        CONSTRAINT [CK_IdentityAnonymousRateLimits_Window]
            CHECK ([WindowEndsAtUtc] > [WindowStartedAtUtc]),
        CONSTRAINT [CK_IdentityAnonymousRateLimits_Retention]
            CHECK ([RetainUntilUtc] >= [WindowEndsAtUtc])
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_IdentityAnonymousRateLimits_RetainUntilUtc'
      AND [object_id] = OBJECT_ID(N'[dbo].[IdentityAnonymousRateLimits]')
)
BEGIN
    CREATE INDEX [IX_IdentityAnonymousRateLimits_RetainUntilUtc]
        ON [dbo].[IdentityAnonymousRateLimits] ([RetainUntilUtc]);
END;

EXEC(N'
CREATE OR ALTER TRIGGER [dbo].[TR_IdentityAuditEvents_AppendOnly]
ON [dbo].[IdentityAuditEvents]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51004, ''Identity audit events are append-only and cannot be updated or deleted.'', 1;
END');

EXEC(N'
CREATE OR ALTER TRIGGER [dbo].[TR_IdentityBootstrapState_PermanentSeal]
ON [dbo].[IdentityBootstrapState]
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted WHERE [Id] = 1)
    BEGIN
        THROW 51005, ''The identity bootstrap seal is permanent and cannot be updated or deleted.'', 1;
    END;
END');
```

Exact SQL errors:

- `51004`: `Identity audit events are append-only and cannot be updated or deleted.`
- `51005`: `The identity bootstrap seal is permanent and cannot be updated or deleted.`

## Exact `DownSql`

```sql
SET XACT_ABORT ON;
DROP TRIGGER IF EXISTS [dbo].[TR_IdentityBootstrapState_PermanentSeal];
DROP TRIGGER IF EXISTS [dbo].[TR_IdentityAuditEvents_AppendOnly];
DROP TABLE IF EXISTS [dbo].[IdentityAnonymousRateLimits];
```

## T18 migration and snapshot instructions

1. Generate the model snapshot from the landed `IdentityAnonymousRateLimit` configuration.
2. In migration `Up`, use the full `InstallSql` as the sole migration operation for these owned
   objects. Remove or suppress duplicate generated `CreateTable` and `CreateIndex` operations for
   `IdentityAnonymousRateLimits`.
3. In migration `Down`, use the full `DownSql` as the sole migration operation for these owned
   objects. Remove or suppress a duplicate generated `DropTable` operation for
   `IdentityAnonymousRateLimits`.
4. Do not alter the constants, object names, column definitions, constraint/index definitions,
   trigger predicates, error numbers, error text, or statement order.

This handoff contains no migration or snapshot implementation.
