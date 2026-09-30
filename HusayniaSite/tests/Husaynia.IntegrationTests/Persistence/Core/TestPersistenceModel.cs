using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.IntegrationTests.Persistence.Core;

internal sealed class TestHusayniaDbContext(DbContextOptions<TestHusayniaDbContext> options)
    : HusayniaDbContext(
        options,
        [typeof(HusayniaDbContext).Assembly, typeof(TestHusayniaDbContext).Assembly])
{
    internal DbSet<TestAggregate> TestAggregates => Set<TestAggregate>();
}

internal sealed class TestAggregate
{
    private TestAggregate()
    {
    }

    internal TestAggregate(string value)
    {
        Id = Guid.NewGuid();
        Value = value;
    }

    public Guid Id { get; private set; }

    public string Value { get; internal set; } = string.Empty;
}

internal sealed class TestAggregateConfiguration
    : MutableEntityConfiguration<TestAggregate>
{
    protected override void ConfigureMutableEntity(
        EntityTypeBuilder<TestAggregate> builder)
    {
        builder.ToTable("T03TestAggregates");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Value).HasMaxLength(200).IsRequired();
    }
}
