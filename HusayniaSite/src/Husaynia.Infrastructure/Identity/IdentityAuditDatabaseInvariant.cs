namespace Husaynia.Infrastructure.Identity;

public static class IdentityAuditDatabaseInvariant
{
    public const string TriggerName = "TR_IdentityAuditEvents_AppendOnly";
    public const string BootstrapSealTriggerName = "TR_IdentityBootstrapState_PermanentSeal";
    public const string RateLimitTableName = "IdentityAnonymousRateLimits";

    public const string InstallSql =
        """
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
        """;

    public const string DownSql =
        """
        SET XACT_ABORT ON;
        DROP TRIGGER IF EXISTS [dbo].[TR_IdentityBootstrapState_PermanentSeal];
        DROP TRIGGER IF EXISTS [dbo].[TR_IdentityAuditEvents_AppendOnly];
        DROP TABLE IF EXISTS [dbo].[IdentityAnonymousRateLimits];
        """;

    // T18 exclusively owns applying InstallSql/DownSql in migration Up/Down. The forward operation
    // is additive and backward compatible. Rollback removes only these triggers and ephemeral
    // limiter state; account, bootstrap-seal, and audit rows remain. On a large database, trigger
    // replacement briefly takes schema locks but does not scan or rewrite existing rows; table
    // creation is metadata-only because the limiter table is new. Runtime startup must never execute
    // schema DDL or require ALTER permission. EnsureCreated-based integration fixtures may execute
    // these constants.
}
