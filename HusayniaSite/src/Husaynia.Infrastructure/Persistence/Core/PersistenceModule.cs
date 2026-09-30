using Husaynia.Application.Contracts;
using Husaynia.Infrastructure.Persistence.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.Infrastructure.Persistence.Core;

public sealed class PersistenceModule : IHusayniaModule
{
    public const string ConnectionStringName = "HusayniaDatabase";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        services.AddDbContext<HusayniaDbContext>(options =>
            options.UseSqlServer(connectionString));
        services.AddScoped<IHusayniaUnitOfWork, EfHusayniaUnitOfWork>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
    }
}

public sealed class PersistenceConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.IsNullOrWhiteSpace(
            configuration.GetConnectionString(PersistenceModule.ConnectionStringName))
            ? [$"ConnectionStrings:{PersistenceModule.ConnectionStringName} is required."]
            : [];
    }
}
