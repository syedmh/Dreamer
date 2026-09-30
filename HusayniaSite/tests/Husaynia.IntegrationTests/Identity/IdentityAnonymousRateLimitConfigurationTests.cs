using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityAnonymousRateLimitConfigurationTests
{
    private static readonly string FingerprintKey =
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    public static TheoryData<string, string?> InvalidValues => new()
    {
        { "Identity:AnonymousRateLimit:PermitLimit", null },
        { "Identity:AnonymousRateLimit:PermitLimit", "0" },
        { "Identity:AnonymousRateLimit:PermitLimit", "-1" },
        { "Identity:AnonymousRateLimit:PermitLimit", "1001" },
        { "Identity:AnonymousRateLimit:PermitLimit", "2147483648" },
        { "Identity:AnonymousRateLimit:PermitLimit", "not-an-integer" },
        { "Identity:AnonymousRateLimit:Window", null },
        { "Identity:AnonymousRateLimit:Window", "00:00:00" },
        { "Identity:AnonymousRateLimit:Window", "-00:00:01" },
        { "Identity:AnonymousRateLimit:Window", "01:00:00.0000001" },
        { "Identity:AnonymousRateLimit:Window", "not-a-timespan" },
        { "Identity:AnonymousRateLimit:Retention", null },
        { "Identity:AnonymousRateLimit:Retention", "00:04:59" },
        { "Identity:AnonymousRateLimit:Retention", "-00:00:01" },
        { "Identity:AnonymousRateLimit:Retention", "30.00:00:00.0000001" },
        { "Identity:AnonymousRateLimit:Retention", "not-a-timespan" },
        { "Identity:AnonymousRateLimit:FingerprintKey", null },
        { "Identity:AnonymousRateLimit:FingerprintKey", "not-base64" },
        { "Identity:AnonymousRateLimit:FingerprintKey", Convert.ToBase64String(new byte[31]) },
    };

    [Theory]
    [MemberData(nameof(InvalidValues))]
    public void MissingMalformedAndOutOfPolicyValuesFailValidation(string key, string? value)
    {
        var values = ValidValues();
        values[key] = value;
        var configuration = BuildConfiguration(values);

        var failures = new IdentityConfigurationValidator().Validate(configuration);

        Assert.Contains(failures, failure => failure.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidFiniteValuesAreRegisteredAndObservable()
    {
        var configuration = BuildConfiguration(ValidValues());
        var services = new ServiceCollection();
        services.AddLogging();

        new IdentityInfrastructureModule().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
        var options = provider.GetRequiredService<IdentityModuleOptions>();
        Assert.Equal(7, options.AnonymousRateLimit.PermitLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), options.AnonymousRateLimit.Window);
        Assert.Equal(TimeSpan.FromDays(2), options.AnonymousRateLimit.Retention);
        Assert.Equal(32, options.AnonymousRateLimit.FingerprintKey.Length);

        using var scope = provider.CreateScope();
        Assert.IsType<SqlIdentityAnonymousRateLimiter>(
            scope.ServiceProvider.GetRequiredService<IIdentityAnonymousRateLimiter>());
    }

    private static Dictionary<string, string?> ValidValues() =>
        new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:HusayniaDatabase"] =
                "Server=(localdb)\\MSSQLLocalDB;Database=ConfigurationOnly;Integrated Security=true;",
            ["Identity:Bootstrap:Enabled"] = "false",
            ["Identity:InvitationLifetime"] = "7.00:00:00",
            ["Identity:AnonymousRateLimit:PermitLimit"] = "7",
            ["Identity:AnonymousRateLimit:Window"] = "00:05:00",
            ["Identity:AnonymousRateLimit:Retention"] = "2.00:00:00",
            ["Identity:AnonymousRateLimit:FingerprintKey"] = FingerprintKey,
        };

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
