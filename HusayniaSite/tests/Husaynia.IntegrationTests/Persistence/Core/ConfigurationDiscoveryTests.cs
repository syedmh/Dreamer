using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.IntegrationTests.Persistence.Core;

public sealed class ConfigurationDiscoveryTests
{
    [Fact]
    public async Task DiscoversConfigurationsAndAppliesManagedConcurrencyFields()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(nameof(DiscoversConfigurationsAndAppliesManagedConcurrencyFields));
        await using var context = database.CreateContext();

        var entityType = context.Model.FindEntityType(typeof(TestAggregate));

        Assert.NotNull(entityType);
        Assert.Equal("T03TestAggregates", entityType.GetTableName());
        Assert.True(entityType.FindProperty(PersistencePropertyNames.RowVersion)!.IsConcurrencyToken);
        Assert.Equal(
            Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate,
            entityType.FindProperty(PersistencePropertyNames.RowVersion)!.ValueGenerated);
        Assert.NotNull(entityType.FindProperty(PersistencePropertyNames.CreatedAtUtc));
        Assert.NotNull(entityType.FindProperty(PersistencePropertyNames.UpdatedAtUtc));

        var entity = new TestAggregate("configured");
        context.Add(entity);
        await context.SaveChangesAsync();

        Assert.Equal(
            TimeSpan.Zero,
            context.Entry(entity)
                .Property<DateTimeOffset>(PersistencePropertyNames.CreatedAtUtc)
                .CurrentValue.Offset);
        Assert.NotEmpty(
            context.Entry(entity)
                .Property<byte[]>(PersistencePropertyNames.RowVersion)
                .CurrentValue);
    }

    [Fact]
    public async Task ManagedTimestampsPreserveCreatedAndAdvanceUpdatedOnModification()
    {
        await using var database =
            await SqlServerTestDatabase.CreateAsync(
                nameof(ManagedTimestampsPreserveCreatedAndAdvanceUpdatedOnModification));
        Guid id;
        DateTimeOffset createdAt;
        DateTimeOffset originalUpdatedAt;

        await using (var context = database.CreateContext())
        {
            var entity = new TestAggregate("initial");
            context.Add(entity);
            await context.SaveChangesAsync();
            id = entity.Id;
            createdAt = context.Entry(entity)
                .Property<DateTimeOffset>(PersistencePropertyNames.CreatedAtUtc)
                .CurrentValue;
            originalUpdatedAt = context.Entry(entity)
                .Property<DateTimeOffset>(PersistencePropertyNames.UpdatedAtUtc)
                .CurrentValue;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(20));

        await using (var context = database.CreateContext())
        {
            var entity = await context.TestAggregates.SingleAsync(candidate => candidate.Id == id);
            entity.Value = "updated";
            await context.SaveChangesAsync();
        }

        await using var verification = database.CreateContext();
        var persisted = await verification.TestAggregates.SingleAsync(candidate => candidate.Id == id);
        var entry = verification.Entry(persisted);
        Assert.Equal(
            createdAt,
            entry.Property<DateTimeOffset>(PersistencePropertyNames.CreatedAtUtc).CurrentValue);
        Assert.True(
            entry.Property<DateTimeOffset>(PersistencePropertyNames.UpdatedAtUtc).CurrentValue >
            originalUpdatedAt);
    }

    [Fact]
    public void DatabaseNamesAreStableSanitizedAndBounded()
    {
        var first = DatabaseName.Create("Husaynia Site", "configuration/discovery");
        var second = DatabaseName.Create("Husaynia Site", "configuration/discovery");

        Assert.Equal(first, second);
        Assert.Matches("^[A-Za-z0-9_]+$", first);
        Assert.True(first.Length <= 120);
    }

    [Fact]
    public void DesignTimeFactoryUsesDeterministicSafeLocalDefaults()
    {
        var prior = Environment.GetEnvironmentVariable(
            HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                null);

            using var context = new HusayniaDesignTimeDbContextFactory().CreateDbContext([]);
            var builder = new SqlConnectionStringBuilder(
                context.Database.GetConnectionString());

            Assert.Equal(@"(localdb)\MSSQLLocalDB", builder.DataSource);
            Assert.Equal(
                DatabaseName.Create("HusayniaSite", "DesignTime"),
                builder.InitialCatalog);
            Assert.True(builder.IntegratedSecurity);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                prior);
        }
    }

    [Theory]
    [InlineData("Server=sql.example.test;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=localhost.example.test;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=127.0.0.1.example.test;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    public void DesignTimeFactoryRejectsNonLocalOrDeceptiveHosts(string connectionString)
    {
        var prior = Environment.GetEnvironmentVariable(
            HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                connectionString);

            Assert.Throws<InvalidOperationException>(
                () => new HusayniaDesignTimeDbContextFactory().CreateDbContext([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                prior);
        }
    }

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=.;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData(@"Server=.\SQLEXPRESS;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=localhost;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData(@"Server=localhost\SQLEXPRESS;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=localhost,1433;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=(local);Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=127.0.0.1;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=127.0.0.1,1433;Database=Husaynia;Integrated Security=true;Encrypt=false")]
    public void DesignTimeFactoryAcceptsOnlyApprovedLocalForms(string connectionString)
    {
        var prior = Environment.GetEnvironmentVariable(
            HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                connectionString);

            using var context = new HusayniaDesignTimeDbContextFactory().CreateDbContext([]);
            Assert.Equal(
                "Husaynia",
                new SqlConnectionStringBuilder(
                    context.Database.GetConnectionString()).InitialCatalog);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                prior);
        }
    }

    [Theory]
    [InlineData("Server=localhostx;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=xlocalhost;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=127.0.0.10;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=127.0.0.1x;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=localhost,0;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=localhost,65536;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=localhost,abc;Database=Husaynia;Integrated Security=true")]
    [InlineData(@"Server=localhost\bad-name;Database=Husaynia;Integrated Security=true")]
    [InlineData(@"Server=(localdb)\Other;Database=Husaynia;Integrated Security=true")]
    [InlineData(@"Server=np:\\.\pipe\sql\query;Database=Husaynia;Integrated Security=true")]
    [InlineData("Server=localhost;Database=Husaynia;User ID=sa;Password=secret")]
    [InlineData("Server=localhost;Database=Husaynia;Integrated Security=false")]
    [InlineData("Server=localhost;Database=Husaynia;Integrated Security=true;Network Library=dbnmpntw")]
    [InlineData("not-a-connection-string")]
    public void DesignTimeFactoryRejectsUnapprovedOrMalformedForms(string connectionString)
    {
        var prior = Environment.GetEnvironmentVariable(
            HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                connectionString);

            Assert.Throws<InvalidOperationException>(
                () => new HusayniaDesignTimeDbContextFactory().CreateDbContext([]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                HusayniaDesignTimeDbContextFactory.ConnectionStringEnvironmentVariable,
                prior);
        }
    }
}
