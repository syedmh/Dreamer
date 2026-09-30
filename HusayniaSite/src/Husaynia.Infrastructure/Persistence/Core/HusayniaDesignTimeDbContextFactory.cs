using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Husaynia.Infrastructure.Persistence.Core;

public sealed class HusayniaDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<HusayniaDbContext>
{
    public const string ConnectionStringEnvironmentVariable =
        "HUSAYNIA_DESIGNTIME_CONNECTION_STRING";

    public HusayniaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        var builder = string.IsNullOrWhiteSpace(connectionString)
            ? CreateLocalDbConnectionString()
            : ValidateLocalConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<HusayniaDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        return new HusayniaDbContext(options);
    }

    private static SqlConnectionStringBuilder CreateLocalDbConnectionString() =>
        new()
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = DatabaseName.Create("HusayniaSite", "DesignTime"),
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        };

    private static SqlConnectionStringBuilder ValidateLocalConnectionString(
        string connectionString)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "The design-time SQL Server connection string is malformed.",
                exception);
        }

        if (!IsApprovedLocalDataSource(builder.DataSource) ||
            !builder.IntegratedSecurity ||
            !string.IsNullOrWhiteSpace(builder.UserID) ||
            !string.IsNullOrWhiteSpace(builder.Password) ||
            builder.Authentication != SqlAuthenticationMethod.NotSpecified ||
            HasNetworkLibraryOverride(builder) ||
            !string.IsNullOrWhiteSpace(builder.AttachDBFilename))
        {
            throw new InvalidOperationException(
                "Design-time persistence tooling accepts only integrated-security local SQL Server connections.");
        }

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            builder.InitialCatalog = DatabaseName.Create("HusayniaSite", "DesignTime");
        }

        return builder;
    }

    private static bool HasNetworkLibraryOverride(SqlConnectionStringBuilder builder) =>
        builder.ContainsKey("Network Library") &&
        !string.IsNullOrWhiteSpace(
            Convert.ToString(
                builder["Network Library"],
                System.Globalization.CultureInfo.InvariantCulture));

    private static bool IsApprovedLocalDataSource(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource) ||
            !string.Equals(dataSource, dataSource.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (dataSource.Equals(
                @"(localdb)\MSSQLLocalDB",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsApprovedHostForm(dataSource, ".") ||
               IsApprovedHostForm(dataSource, "(local)") ||
               IsApprovedHostForm(dataSource, "localhost") ||
               IsApprovedHostForm(dataSource, "127.0.0.1");
    }

    private static bool IsApprovedHostForm(string dataSource, string host)
    {
        if (dataSource.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dataSource.StartsWith($"{host}\\", StringComparison.OrdinalIgnoreCase))
        {
            var instance = dataSource[(host.Length + 1)..];
            return instance is { Length: > 0 and <= 128 } &&
                   instance.All(character =>
                       char.IsAsciiLetterOrDigit(character) ||
                       character is '_' or '$');
        }

        if (dataSource.StartsWith($"{host},", StringComparison.OrdinalIgnoreCase))
        {
            var portText = dataSource[(host.Length + 1)..];
            return int.TryParse(
                       portText,
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var port) &&
                   port is >= 1 and <= 65535;
        }

        return false;
    }
}
