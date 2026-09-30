using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.Infrastructure.Forms;

public sealed class FormsModule : IHusayniaModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = FormsConfiguration.Read(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddScoped<FormSubmissionValidator>();
        services.AddScoped<EfFormsStore>();
        services.AddScoped<IActiveFormDefinitionReader>(
            provider => provider.GetRequiredService<EfFormsStore>());
        services.AddScoped<IFormSubmissionStore>(
            provider => provider.GetRequiredService<EfFormsStore>());
        services.AddScoped<IFormDeliveryStore>(
            provider => provider.GetRequiredService<EfFormsStore>());
        services.AddScoped<IFormAdministrationStore>(
            provider => provider.GetRequiredService<EfFormsStore>());
        services.AddScoped<IFormSubmissionService, FormSubmissionService>();
        services.AddScoped<IFormAdministration, FormAdministrationService>();
        services.AddScoped<IFormsRateLimiter, SqlFormsRateLimiter>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, FormDeliveryJobHandler>());
        services.AddScoped<IFormsDeliveryJobCoordinator, FormsDeliveryJobCoordinator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IRetentionTarget, FormSubmissionRetentionTarget>());
        services.AddScoped<IOutboundMessageSender>(provider =>
            options.Enabled && options.DeliveryMode == FormDeliveryMode.Pickup
                ? new PickupFormMessageSender(
                    options,
                    provider.GetRequiredService<TimeProvider>())
                : new DisabledFormMessageSender());
    }
}
