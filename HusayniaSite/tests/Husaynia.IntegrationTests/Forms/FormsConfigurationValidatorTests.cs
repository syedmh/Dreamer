using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Infrastructure.Forms;
using Husaynia.IntegrationTests.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.IntegrationTests.Forms;

public sealed class FormsConfigurationValidatorTests
{
    [Fact]
    public void DisabledByDefaultRequiresNoLiveConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();

        var failures = new FormsConfigurationValidator().Validate(configuration);

        Assert.Empty(failures);
    }

    [Fact]
    public void EnabledConfigurationRequiresExactKeyAndBoundedPolicies()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forms:Enabled"] = "true",
                ["Forms:FingerprintKey"] = Convert.ToBase64String(new byte[31]),
                ["Forms:DuplicateWindow"] = "00:00:30",
                ["Forms:RateLimit:PermitLimit"] = "0",
                ["Forms:RateLimit:Window"] = "02:00:00",
                ["Forms:RetentionPeriod"] = "00:00:00",
                ["Forms:Delivery:Mode"] = "Network",
            })
            .Build();

        var failures = new FormsConfigurationValidator().Validate(configuration);

        Assert.Contains(failures, failure => failure.Contains("32 bytes", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("DuplicateWindow", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("PermitLimit", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("Delivery:Mode", StringComparison.Ordinal));
    }

    [Fact]
    public void ModuleRegistersSubmissionDeliveryRetentionAndSafeDisabledSink()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        new FormsModule().AddServices(services, configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IFormSubmissionService));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IFormAdministration));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IJobHandler) &&
            descriptor.ImplementationType == typeof(FormDeliveryJobHandler));
        using var provider = services.BuildServiceProvider();
        Assert.IsType<DisabledFormMessageSender>(
            provider.GetRequiredService<IOutboundMessageSender>());
    }

    [Fact]
    public void DisabledFormsStillRejectsInvalidDeliveryModeAndLiveSecrets()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forms:Enabled"] = "false",
                ["Forms:Delivery:Mode"] = "Network",
                ["Forms:Delivery:ApiKey"] = "not-a-live-secret",
            })
            .Build();

        var failures = new FormsConfigurationValidator().Validate(configuration);

        Assert.Contains(failures, failure =>
            failure.Contains("Delivery:Mode", StringComparison.Ordinal));
        Assert.Contains(failures, failure =>
            failure.Contains("ApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigurationEnvironmentOverrideIsIgnored()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forms:EnvironmentName"] = "Development",
            })
            .Build();

        var failures = new FormsConfigurationValidator().Validate(configuration);

        Assert.Empty(failures);
    }

    [Fact]
    public void DisabledFormsCannotActivateConfiguredPickupSink()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Forms:Enabled"] = "false",
                ["Forms:Delivery:Mode"] = "Pickup",
                ["Forms:Delivery:PickupDirectory"] = Path.GetFullPath("forms-disabled-pickup"),
                ["Forms:Delivery:PickupDestinationKey"] = "test-destination",
            })
            .Build();
        var services = new ServiceCollection();

        new FormsModule().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<DisabledFormMessageSender>(
            provider.GetRequiredService<IOutboundMessageSender>());
    }

    [Fact]
    public async Task DiscoveredModelContainsExactTablesAndDuplicateFence()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(DiscoveredModelContainsExactTablesAndDuplicateFence));
        await using var context = database.CreateContext();

        var tables = new[]
        {
            typeof(Husaynia.Domain.Forms.FormDefinition),
            typeof(Husaynia.Domain.Forms.FormDefinitionVersion),
            typeof(Husaynia.Domain.Forms.FormField),
            typeof(Husaynia.Domain.Forms.FormSubmission),
            typeof(Husaynia.Domain.Forms.FormSubmissionValue),
            typeof(Husaynia.Domain.Forms.FormDeliveryAttempt),
            typeof(Husaynia.Domain.Forms.FormRateLimit),
        }.Select(type => context.Model.FindEntityType(type)!.GetTableName()!).ToArray();
        var submission = context.Model.FindEntityType(
            typeof(Husaynia.Domain.Forms.FormSubmission))!;
        var duplicateIndex = submission.GetIndexes().Single(index =>
            index.GetDatabaseName() ==
                "UX_FormSubmissions_DefinitionVersionId_Fingerprint_Window");

        Assert.Equal(
            [
                "FormDefinitions",
                "FormDefinitionVersions",
                "FormFields",
                "FormSubmissions",
                "FormSubmissionValues",
                "FormDeliveryAttempts",
                "FormRateLimits",
            ],
            tables);
        Assert.True(duplicateIndex.IsUnique);
        Assert.Equal(
            ["DefinitionVersionId", "DuplicateFingerprint", "DuplicateWindowStartUtc"],
            duplicateIndex.Properties.Select(property => property.Name));
        Assert.Equal(
            "binary(32)",
            submission.FindProperty("DuplicateFingerprint")!.GetColumnType());
    }
}
