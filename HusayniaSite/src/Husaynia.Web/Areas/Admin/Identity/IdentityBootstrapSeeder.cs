using System.Data;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Husaynia.Web.Areas.Admin.Identity;

internal sealed class IdentityBootstrapSeeder(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment hostEnvironment,
    IdentityModuleOptions options) : IHostedService
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly IHostEnvironment hostEnvironment =
        hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
    private readonly IdentityModuleOptions options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HusayniaIdentityDbContext>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<HusayniaIdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();

        if (!options.Bootstrap.Enabled ||
            !hostEnvironment.EnvironmentName.Equals(
                options.Bootstrap.EnvironmentName,
                StringComparison.Ordinal))
        {
            await EnsureFrozenRolesAsync(roleManager).ConfigureAwait(false);
            return;
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        // The update/range lock makes the absent-row case single-writer across concurrent app
        // instances. T18 owns creating this table and its primary key migration.
        await dbContext.Database.ExecuteSqlRawAsync(
                """
                SELECT [Id]
                FROM [IdentityBootstrapState] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = 1
                """,
                cancellationToken)
            .ConfigureAwait(false);

        if (await dbContext.Set<IdentityBootstrapState>()
                .AsNoTracking()
                .AnyAsync(
                    state => state.Id == IdentityBootstrapState.PermanentSealId,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnsureFrozenRolesAsync(roleManager).ConfigureAwait(false);

        var user = await userManager.FindByEmailAsync(options.Bootstrap.AdminEmail)
            .ConfigureAwait(false);
        var wasCreated = false;
        if (user is null)
        {
            user = new HusayniaIdentityUser(options.Bootstrap.AdminEmail);
            var createUser = await userManager.CreateAsync(user, options.Bootstrap.AdminPassword)
                .ConfigureAwait(false);
            if (!createUser.Succeeded)
            {
                throw new InvalidOperationException(
                    "Identity bootstrap admin creation failed: " +
                    string.Join(", ", createUser.Errors.Select(error => error.Code)));
            }

            wasCreated = true;
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            var update = await userManager.UpdateAsync(user).ConfigureAwait(false);
            if (!update.Succeeded)
            {
                throw new InvalidOperationException(
                    "Identity bootstrap admin confirmation failed: " +
                    string.Join(", ", update.Errors.Select(error => error.Code)));
            }
        }

        if (!await userManager.IsInRoleAsync(user, RoleNames.SiteAdministrator).ConfigureAwait(false))
        {
            var addRole = await userManager.AddToRoleAsync(user, RoleNames.SiteAdministrator)
                .ConfigureAwait(false);
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException(
                    "Identity bootstrap admin role assignment failed: " +
                    string.Join(", ", addRole.Errors.Select(error => error.Code)));
            }
        }

        var now = DateTimeOffset.UtcNow;
        dbContext.Add(new IdentityBootstrapState(
            hostEnvironment.EnvironmentName,
            user.Id,
            now));
        dbContext.Add(CreateBootstrapAudit(
            "identity.bootstrap.administrator.assigned",
            user.Id,
            now,
            new Dictionary<string, object?>
            {
                ["created"] = wasCreated,
                ["role"] = RoleNames.SiteAdministrator,
            }));
        dbContext.Add(CreateBootstrapAudit(
            "identity.bootstrap.sealed",
            user.Id,
            now,
            new Dictionary<string, object?>
            {
                ["environment"] = hostEnvironment.EnvironmentName,
                ["sealId"] = IdentityBootstrapState.PermanentSealId,
            }));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureFrozenRolesAsync(
        RoleManager<HusayniaIdentityRole> roleManager)
    {
        foreach (var roleName in RoleNames.All.Order(StringComparer.Ordinal))
        {
            if (await roleManager.RoleExistsAsync(roleName).ConfigureAwait(false))
            {
                continue;
            }

            var createRole = await roleManager.CreateAsync(new HusayniaIdentityRole(roleName))
                .ConfigureAwait(false);
            if (!createRole.Succeeded &&
                !await roleManager.RoleExistsAsync(roleName).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Identity role bootstrap failed for {roleName}: " +
                    string.Join(", ", createRole.Errors.Select(error => error.Code)));
            }
        }
    }

    private static AuditEvent CreateBootstrapAudit(
        string action,
        Guid administratorUserId,
        DateTimeOffset occurredAtUtc,
        IReadOnlyDictionary<string, object?> details) =>
        new(
            actorId: null,
            rolesJson: "[]",
            action,
            targetType: "IdentityUser",
            targetId: administratorUserId.ToString(),
            PrivilegedAttemptOutcome.Allowed,
            correlationId: $"bootstrap-{administratorUserId:N}",
            occurredAtUtc,
            JsonSerializer.Serialize(details));
}
