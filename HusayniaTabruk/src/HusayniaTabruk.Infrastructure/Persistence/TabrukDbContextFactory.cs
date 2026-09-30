using System.Data;
using HusayniaTabruk.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace HusayniaTabruk.Infrastructure.Persistence;

/// <summary>
/// Design-time migration factory.  EF is intentionally limited to disposable databases; the
/// checked-in owner scripts are the only supported production T8 interface.
/// </summary>
public sealed class TabrukDbContextFactory : IDesignTimeDbContextFactory<TabrukDbContext>
{
    public TabrukDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("TABRUK_MIGRATIONS_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set TABRUK_MIGRATIONS_CONNECTION to generate or execute migrations.");

        return TabrukDbContextOptions.Create(connectionString);
    }
}

public static class TabrukDbContextOptions
{
    private const string SearchPathRequiredMessage =
        "TABRUK_MIGRATIONS_CONNECTION requires exactly one Search Path schema.";
    private const string TargetSchemaRequiredMessage =
        "TABRUK_MIGRATIONS_CONNECTION requires exactly one tabruk.target_schema option.";
    private const string DisposableEfRequiredMessage =
        "TABRUK_MIGRATIONS_CONNECTION requires exactly one -c tabruk.disposable_ef=on option; EF migrations are disposable-database only.";
    private const string InvalidSchemaMessage =
        "TABRUK_MIGRATIONS_CONNECTION schema must be one unquoted lowercase PostgreSQL identifier.";
    private const string SchemaMismatchMessage =
        "TABRUK_MIGRATIONS_CONNECTION Search Path and tabruk.target_schema must match.";
    private const string TargetSchemaOptionPrefix = "tabruk.target_schema=";
    private const string DisposableEfOption = "tabruk.disposable_ef=on";

    public static TabrukDbContext Create(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(SearchPathRequiredMessage);
        }

        NpgsqlConnectionStringBuilder connectionStringBuilder;
        try
        {
            connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(SearchPathRequiredMessage, exception);
        }

        if (CountConnectionStringKey(connectionString, "Search Path", "SearchPath") != 1
            || string.IsNullOrWhiteSpace(connectionStringBuilder.SearchPath))
        {
            throw new InvalidOperationException(SearchPathRequiredMessage);
        }

        string[] targetSchemas = ParseOptionValues(
            connectionStringBuilder.Options,
            TargetSchemaOptionPrefix);
        if (targetSchemas.Length != 1)
        {
            throw new InvalidOperationException(TargetSchemaRequiredMessage);
        }

        string[] disposableOptions = ParseOptionValues(
            connectionStringBuilder.Options,
            DisposableEfOption);
        if (disposableOptions.Length != 1
            || !string.Equals(disposableOptions[0], DisposableEfOption, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(DisposableEfRequiredMessage);
        }

        string expectedSchema = connectionStringBuilder.SearchPath;
        string targetSchema = targetSchemas[0];
        if (!IsValidSchema(expectedSchema) || !IsValidSchema(targetSchema))
        {
            throw new InvalidOperationException(InvalidSchemaMessage);
        }

        if (!string.Equals(expectedSchema, targetSchema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(SchemaMismatchMessage);
        }

        NpgsqlConnection connection = new(connectionString);
        connection.StateChange += AttestMigrationStateOnOpen;

        DbContextOptions<TabrukDbContext> options = new DbContextOptionsBuilder<TabrukDbContext>()
            .UseNpgsql(
                connection,
                contextOwnsConnection: true,
                npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(TabrukDbContext).Assembly.FullName);
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", expectedSchema);
                })
            .EnableDetailedErrors()
            .Options;

        return new TabrukDbContext(options);

        void AttestMigrationStateOnOpen(object? sender, StateChangeEventArgs stateChange)
        {
            if (stateChange.CurrentState != ConnectionState.Open)
            {
                return;
            }

            using NpgsqlCommand disposableCommand = new(
                PostgresLeastPrivilegeCatalog.DisposableEfGuardSql,
                connection);
            disposableCommand.ExecuteNonQuery();

            using NpgsqlCommand schemaCommand = new(
                PostgresLeastPrivilegeCatalog.SchemaPreflightSql,
                connection);
            schemaCommand.ExecuteNonQuery();

            string classification;
            using (NpgsqlCommand classificationCommand = new(
                PostgresLeastPrivilegeCatalog.ClassifyInitialInventorySql,
                connection))
            {
                classification = (string?)classificationCommand.ExecuteScalar()
                    ?? throw new InvalidOperationException(
                        "T8 disposable inventory classification returned no result.");
            }

            if (classification == "invalid")
            {
                using NpgsqlCommand invalidCommand = new(
                    PostgresLeastPrivilegeCatalog.InvalidInitialInventorySql,
                    connection);
                invalidCommand.ExecuteNonQuery();
            }

            if (classification == "pristine")
            {
                connection.StateChange -= AttestMigrationStateOnOpen;
                return;
            }

            if (classification != "initial-inventory")
            {
                using (NpgsqlTransaction revokeTransaction = connection.BeginTransaction())
                {
                    using NpgsqlCommand revokeLockCommand = new(
                        PostgresLeastPrivilegeCatalog.MigrationHistoryWriteLockSql(expectedSchema),
                        connection,
                        revokeTransaction);
                    revokeLockCommand.ExecuteNonQuery();
                    using NpgsqlCommand historyHardeningCommand = new(
                        PostgresLeastPrivilegeCatalog.RevokeMigrationHistoryPrivilegesSql,
                        connection,
                        revokeTransaction);
                    historyHardeningCommand.ExecuteNonQuery();
                    revokeTransaction.Commit();
                }

                using (NpgsqlTransaction structureTransaction = connection.BeginTransaction())
                {
                    using NpgsqlCommand structureLockCommand = new(
                        PostgresLeastPrivilegeCatalog.MigrationHistoryWriteLockSql(expectedSchema),
                        connection,
                        structureTransaction);
                    structureLockCommand.ExecuteNonQuery();
                    using NpgsqlCommand bootstrapStructureCommand = new(
                        PostgresLeastPrivilegeCatalog.MigrationHistoryStructureAttestationSql,
                        connection,
                        structureTransaction);
                    bootstrapStructureCommand.ExecuteNonQuery();
                    using NpgsqlCommand bootstrapPrivilegeCommand = new(
                        PostgresLeastPrivilegeCatalog.MigrationHistoryPrivilegeAttestationSql,
                        connection,
                        structureTransaction);
                    bootstrapPrivilegeCommand.ExecuteNonQuery();
                    using NpgsqlCommand emptyHistoryCommand = new(
                        PostgresLeastPrivilegeCatalog.EmptyBootstrapHistoryAttestationSql,
                        connection,
                        structureTransaction);
                    emptyHistoryCommand.ExecuteNonQuery();
                    structureTransaction.Commit();
                }

                connection.StateChange -= AttestMigrationStateOnOpen;
                return;
            }

            using (NpgsqlTransaction hardeningTransaction = connection.BeginTransaction())
            {
                using NpgsqlCommand lockCommand = new(
                    PostgresLeastPrivilegeCatalog.ManagedTableTopologyLockSqlForSchema(expectedSchema),
                    connection,
                    hardeningTransaction);
                lockCommand.ExecuteNonQuery();

                using NpgsqlCommand structureCommand = new(
                    PostgresLeastPrivilegeCatalog.MigrationHistoryStructureAttestationSql,
                    connection,
                    hardeningTransaction);
                structureCommand.ExecuteNonQuery();

                using NpgsqlCommand topologyCommand = new(
                    PostgresLeastPrivilegeCatalog.ManagedTableTopologyAttestationSql,
                    connection,
                    hardeningTransaction);
                topologyCommand.ExecuteNonQuery();

                using NpgsqlCommand hardeningCommand = new(
                    PostgresLeastPrivilegeCatalog.ApplyRuntimeLeastPrivilegeSql,
                    connection,
                    hardeningTransaction);
                hardeningCommand.ExecuteNonQuery();
                hardeningTransaction.Commit();
            }

            using (NpgsqlTransaction attestationTransaction = connection.BeginTransaction())
            {
                using NpgsqlCommand lockCommand = new(
                    PostgresLeastPrivilegeCatalog.ManagedTableTopologyLockSqlForSchema(expectedSchema),
                    connection,
                    attestationTransaction);
                lockCommand.ExecuteNonQuery();
                using NpgsqlCommand attestationCommand = new(
                    PostgresLeastPrivilegeCatalog.PreHistoryAttestationSql,
                    connection,
                    attestationTransaction);
                attestationCommand.ExecuteNonQuery();
                attestationTransaction.Commit();
            }
            connection.StateChange -= AttestMigrationStateOnOpen;
        }
    }

    private static int CountConnectionStringKey(
        string connectionString,
        params string[] acceptedKeys)
    {
        int count = 0;
        int segmentStart = 0;
        bool insideSingleQuotes = false;
        bool insideDoubleQuotes = false;

        for (int index = 0; index <= connectionString.Length; index++)
        {
            bool atEnd = index == connectionString.Length;
            char current = atEnd ? '\0' : connectionString[index];
            if (!atEnd && current == '\'' && !insideDoubleQuotes)
            {
                if (insideSingleQuotes
                    && index + 1 < connectionString.Length
                    && connectionString[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                insideSingleQuotes = !insideSingleQuotes;
                continue;
            }

            if (!atEnd && current == '"' && !insideSingleQuotes)
            {
                if (insideDoubleQuotes
                    && index + 1 < connectionString.Length
                    && connectionString[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                insideDoubleQuotes = !insideDoubleQuotes;
                continue;
            }

            if (!atEnd && (current != ';' || insideSingleQuotes || insideDoubleQuotes))
            {
                continue;
            }

            ReadOnlySpan<char> segment = connectionString.AsSpan(segmentStart, index - segmentStart);
            int equalsIndex = segment.IndexOf('=');
            if (equalsIndex >= 0)
            {
                ReadOnlySpan<char> key = segment[..equalsIndex].Trim();
                foreach (string acceptedKey in acceptedKeys)
                {
                    if (key.Equals(
                            acceptedKey.AsSpan(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        break;
                    }
                }
            }

            segmentStart = index + 1;
        }

        return count;
    }

    private static string[] ParseOptionValues(string? options, string prefix)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return [];
        }

        string[] tokens = options.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> values = [];
        for (int index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], "-c", StringComparison.Ordinal))
            {
                continue;
            }

            string setting = tokens[++index];
            if (setting.StartsWith(prefix, StringComparison.Ordinal))
            {
                values.Add(
                    string.Equals(prefix, TargetSchemaOptionPrefix, StringComparison.Ordinal)
                        ? setting[prefix.Length..]
                        : setting);
            }
        }

        return [.. values];
    }

    private static bool IsValidSchema(string schema)
    {
        if (schema.Length is < 1 or > 63
            || schema is "pg_catalog" or "information_schema"
            || schema.StartsWith("pg_", StringComparison.Ordinal)
            || !IsLowercaseAsciiLetter(schema[0]) && schema[0] != '_')
        {
            return false;
        }

        return schema[1..].All(
            character =>
                IsLowercaseAsciiLetter(character)
                || character is >= '0' and <= '9'
                || character is '_' or '$');
    }

    private static bool IsLowercaseAsciiLetter(char character) =>
        character is >= 'a' and <= 'z';
}
