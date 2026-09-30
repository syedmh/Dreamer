using System.Data.Common;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Husaynia.Infrastructure.Identity;

public sealed class IdentityAdministrationStore(
    HusayniaIdentityDbContext dbContext,
    UserManager<HusayniaIdentityUser> userManager,
    RoleManager<HusayniaIdentityRole> roleManager,
    IIdentityAuditFinalizer auditFinalizer,
    TimeProvider timeProvider,
    IdentityModuleOptions options) : IIdentityAdministrationStore
{
    private readonly HusayniaIdentityDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly UserManager<HusayniaIdentityUser> userManager =
        userManager ?? throw new ArgumentNullException(nameof(userManager));
    private readonly RoleManager<HusayniaIdentityRole> roleManager =
        roleManager ?? throw new ArgumentNullException(nameof(roleManager));
    private readonly IIdentityAuditFinalizer auditFinalizer =
        auditFinalizer ?? throw new ArgumentNullException(nameof(auditFinalizer));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IdentityModuleOptions options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async Task<Result<InviteIdentityUserReceipt, IdentityAdministrationError>> InviteAsync(
        InviteIdentityUserCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(audit);

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var expiresAtUtc = now + options.InvitationLifetime;
        var rawToken = HusayniaIdentityToken.CreateRawToken();
        var tokenHash = HusayniaIdentityToken.Hash(rawToken);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var user = await userManager.FindByEmailAsync(command.Email).ConfigureAwait(false);
            var wasCreated = false;
            if (user is null)
            {
                user = new HusayniaIdentityUser(command.Email);
                user.IssueInvitation(tokenHash, now, expiresAtUtc);
                var createResult = await userManager.CreateAsync(user).ConfigureAwait(false);
                if (!createResult.Succeeded)
                {
                    await RollbackAsync(transaction).ConfigureAwait(false);
                    return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                        audit,
                        targetId: null,
                        error: MapIdentityResult(createResult)).ConfigureAwait(false);
                }

                wasCreated = true;
            }
            else
            {
                if (user.IsDisabled)
                {
                    await RollbackAsync(transaction).ConfigureAwait(false);
                    return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                        audit,
                        user.Id.ToString(),
                        new IdentityAdministrationError(
                            "account_disabled",
                            "Disabled accounts cannot be reinvited.")).ConfigureAwait(false);
                }

                if (user.HasPassword)
                {
                    await RollbackAsync(transaction).ConfigureAwait(false);
                    return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                        audit,
                        user.Id.ToString(),
                        new IdentityAdministrationError(
                            "account_already_active",
                            "The account already exists and has completed activation.")).ConfigureAwait(false);
                }

                user.IssueInvitation(tokenHash, now, expiresAtUtc);
                var updateResult = await userManager.UpdateAsync(user).ConfigureAwait(false);
                if (!updateResult.Succeeded)
                {
                    await RollbackAsync(transaction).ConfigureAwait(false);
                    return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                        audit,
                        user.Id.ToString(),
                        MapIdentityResult(updateResult)).ConfigureAwait(false);
                }
            }

            var roleError = await ReplaceRolesAsync(user, command.Roles, cancellationToken)
                .ConfigureAwait(false);
            if (roleError is not null)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                    audit,
                    user.Id.ToString(),
                    roleError).ConfigureAwait(false);
            }

            var view = await ToViewAsync(user, cancellationToken).ConfigureAwait(false);
            await auditFinalizer.FinalizeOnceAsync(
                audit with { TargetId = user.Id.ToString() },
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = wasCreated ? "created" : "reinvited",
                    ["assignedRoles"] = string.Join(",", view.Roles),
                    ["expiresAtUtc"] = expiresAtUtc.ToString("O"),
                },
                token => transaction.CommitAsync(token)).ConfigureAwait(false);

            return Result.Succeed<InviteIdentityUserReceipt, IdentityAdministrationError>(
                new InviteIdentityUserReceipt(view, wasCreated, rawToken, expiresAtUtc));
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                audit,
                targetId: null,
                new IdentityAdministrationError(
                    "duplicate_account",
                    "The identity account already exists.")).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                audit,
                targetId: null,
                new IdentityAdministrationError(
                    "concurrency_conflict",
                    "The account changed after it was loaded.")).ConfigureAwait(false);
        }
        catch (DbException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                audit,
                targetId: null,
                new IdentityAdministrationError(
                    "identity_persistence_failure",
                    "The identity store could not persist the invitation.")).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                audit,
                targetId: null,
                new IdentityAdministrationError(
                    "identity_persistence_failure",
                    "The identity store could not persist the invitation.")).ConfigureAwait(false);
        }
    }

    public async Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> DisableAsync(
        DisableIdentityUserCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(audit);

        var user = await userManager.FindByIdAsync(command.UserId).ConfigureAwait(false);
        if (user is null)
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                command.UserId,
                new IdentityAdministrationError(
                    "account_not_found",
                    "The requested account does not exist.")).ConfigureAwait(false);
        }

        if (user.IsDisabled)
        {
            var currentView = await ToViewAsync(user, cancellationToken).ConfigureAwait(false);
            await auditFinalizer.FinalizeOnceAsync(
                audit with { TargetId = user.Id.ToString() },
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "already_disabled",
                    ["reason"] = command.Reason,
                }).ConfigureAwait(false);
            return Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                new IdentityAccountMutationReceipt(currentView));
        }

        if (!string.Equals(
                command.ExpectedConcurrencyStamp,
                user.ConcurrencyStamp,
                StringComparison.Ordinal))
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                user.Id.ToString(),
                new IdentityAdministrationError(
                    "concurrency_conflict",
                    "The account changed after it was loaded.")).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow().ToUniversalTime();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            user.Disable(now);
            var updateResult = await userManager.UpdateAsync(user).ConfigureAwait(false);
            if (!updateResult.Succeeded)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                    audit,
                    user.Id.ToString(),
                    MapIdentityResult(updateResult)).ConfigureAwait(false);
            }

            var stampResult = await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
            if (!stampResult.Succeeded)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                    audit,
                    user.Id.ToString(),
                    MapIdentityResult(stampResult)).ConfigureAwait(false);
            }

            var view = await ToViewAsync(user, cancellationToken).ConfigureAwait(false);
            await auditFinalizer.FinalizeOnceAsync(
                audit with { TargetId = user.Id.ToString() },
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "disabled",
                    ["reason"] = command.Reason,
                },
                token => transaction.CommitAsync(token)).ConfigureAwait(false);

            return Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                new IdentityAccountMutationReceipt(view));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                user.Id.ToString(),
                new IdentityAdministrationError(
                    "concurrency_conflict",
                    "The account changed after it was loaded.")).ConfigureAwait(false);
        }
        catch (DbException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                user.Id.ToString(),
                new IdentityAdministrationError(
                    "identity_persistence_failure",
                    "The identity store could not disable the account.")).ConfigureAwait(false);
        }
    }

    public async Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> SetRolesAsync(
        SetIdentityUserRolesCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(audit);

        var user = await userManager.FindByIdAsync(command.UserId).ConfigureAwait(false);
        if (user is null)
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                command.UserId,
                new IdentityAdministrationError(
                    "account_not_found",
                    "The requested account does not exist.")).ConfigureAwait(false);
        }

        var currentRoles = (await userManager.GetRolesAsync(user).ConfigureAwait(false))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var requestedRoles = command.Roles.Order(StringComparer.Ordinal).ToArray();
        if (!string.Equals(
                command.ExpectedConcurrencyStamp,
                user.ConcurrencyStamp,
                StringComparison.Ordinal))
        {
            if (currentRoles.SequenceEqual(requestedRoles, StringComparer.Ordinal))
            {
                var currentView = await ToViewAsync(user, cancellationToken).ConfigureAwait(false);
                await auditFinalizer.FinalizeOnceAsync(
                    audit with { TargetId = user.Id.ToString() },
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "already_applied",
                        ["assignedRoles"] = string.Join(",", currentView.Roles),
                    }).ConfigureAwait(false);
                return Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                    new IdentityAccountMutationReceipt(currentView));
            }

            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                user.Id.ToString(),
                new IdentityAdministrationError(
                    "concurrency_conflict",
                    "The account changed after it was loaded.")).ConfigureAwait(false);
        }

        if (currentRoles.SequenceEqual(requestedRoles, StringComparer.Ordinal))
        {
            var currentView = await ToViewAsync(user, cancellationToken).ConfigureAwait(false);
            await auditFinalizer.FinalizeOnceAsync(
                audit with { TargetId = user.Id.ToString() },
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "noop",
                    ["assignedRoles"] = string.Join(",", currentView.Roles),
                }).ConfigureAwait(false);
            return Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                new IdentityAccountMutationReceipt(currentView));
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var roleError = await ReplaceRolesAsync(user, requestedRoles, cancellationToken)
                .ConfigureAwait(false);
            if (roleError is not null)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                    audit,
                    user.Id.ToString(),
                    roleError).ConfigureAwait(false);
            }

            var stampResult = await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
            if (!stampResult.Succeeded)
            {
                await RollbackAsync(transaction).ConfigureAwait(false);
                return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                    audit,
                    user.Id.ToString(),
                    MapIdentityResult(stampResult)).ConfigureAwait(false);
            }

            var view = await ToViewAsync(user, cancellationToken).ConfigureAwait(false);
            await auditFinalizer.FinalizeOnceAsync(
                audit with { TargetId = user.Id.ToString() },
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "updated",
                    ["assignedRoles"] = string.Join(",", view.Roles),
                },
                token => transaction.CommitAsync(token)).ConfigureAwait(false);

            return Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                new IdentityAccountMutationReceipt(view));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                user.Id.ToString(),
                new IdentityAdministrationError(
                    "concurrency_conflict",
                    "The account changed after it was loaded.")).ConfigureAwait(false);
        }
        catch (DbException)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                audit,
                user.Id.ToString(),
                new IdentityAdministrationError(
                    "identity_persistence_failure",
                    "The identity store could not update the assigned roles.")).ConfigureAwait(false);
        }
    }

    public async Task<Result<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>> ReadAuditEventsAsync(
        ReadIdentityAuditEventsQuery query,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = await dbContext.Set<AuditEvent>()
                .AsNoTracking()
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .ThenByDescending(entry => entry.Id)
                .Take(query.Take)
                .Select(entry => new IdentityAuditEventView(
                    entry.Id,
                    entry.ActorId,
                    entry.RolesJson,
                    entry.Action,
                    entry.TargetType,
                    entry.TargetId,
                    entry.Outcome,
                    entry.CorrelationId,
                    entry.OccurredAtUtc,
                    entry.DetailJson))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            await auditFinalizer.FinalizeOnceAsync(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "read",
                    ["take"] = query.Take.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }).ConfigureAwait(false);

            return Result.Succeed<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>(events);
        }
        catch (DbException)
        {
            return await AuditAllowedFailureAsync<IReadOnlyList<IdentityAuditEventView>>(
                audit,
                audit.TargetId,
                new IdentityAdministrationError(
                    "audit_read_failed",
                    "The audit log could not be read.")).ConfigureAwait(false);
        }
    }

    public async Task<Result<IdentityAuditSummaryView, IdentityAdministrationError>> ReadAuditSummaryAsync(
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var total = await dbContext.Set<AuditEvent>()
                .AsNoTracking()
                .CountAsync(cancellationToken)
                .ConfigureAwait(false);
            var allowed = await dbContext.Set<AuditEvent>()
                .AsNoTracking()
                .CountAsync(
                    entry => entry.Outcome == "allowed",
                    cancellationToken)
                .ConfigureAwait(false);
            var denied = total - allowed;
            var latest = await dbContext.Set<AuditEvent>()
                .AsNoTracking()
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .Select(entry => (DateTimeOffset?)entry.OccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            await auditFinalizer.FinalizeOnceAsync(
                audit,
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "summary",
                }).ConfigureAwait(false);

            return Result.Succeed<IdentityAuditSummaryView, IdentityAdministrationError>(
                new IdentityAuditSummaryView(total, allowed, denied, latest));
        }
        catch (DbException)
        {
            return await AuditAllowedFailureAsync<IdentityAuditSummaryView>(
                audit,
                audit.TargetId,
                new IdentityAdministrationError(
                    "audit_read_failed",
                    "The audit summary could not be read.")).ConfigureAwait(false);
        }
    }

    private async Task<IdentityAdministrationError?> ReplaceRolesAsync(
        HusayniaIdentityUser user,
        IReadOnlyCollection<string> requestedRoles,
        CancellationToken cancellationToken)
    {
        foreach (var role in requestedRoles)
        {
            if (!await roleManager.RoleExistsAsync(role).ConfigureAwait(false))
            {
                return new IdentityAdministrationError(
                    "role_not_seeded",
                    "The requested role does not exist in the identity store.");
            }
        }

        var currentRoles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
        var requested = requestedRoles.ToHashSet(StringComparer.Ordinal);
        var current = currentRoles.ToHashSet(StringComparer.Ordinal);
        var toRemove = current.Except(requested, StringComparer.Ordinal).ToArray();
        if (toRemove.Length > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, toRemove).ConfigureAwait(false);
            if (!removeResult.Succeeded)
            {
                return MapIdentityResult(removeResult);
            }
        }

        var toAdd = requested.Except(current, StringComparer.Ordinal).ToArray();
        if (toAdd.Length > 0)
        {
            var addResult = await userManager.AddToRolesAsync(user, toAdd).ConfigureAwait(false);
            if (!addResult.Succeeded)
            {
                return MapIdentityResult(addResult);
            }
        }

        return null;
    }

    private async Task<IdentityUserView> ToViewAsync(
        HusayniaIdentityUser user,
        CancellationToken cancellationToken)
    {
        var roles = (await userManager.GetRolesAsync(user).ConfigureAwait(false))
            .Order(StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new IdentityUserView(
            user.Id.ToString(),
            user.Email ?? string.Empty,
            user.IsDisabled,
            user.TwoFactorEnabled,
            user.HasPassword,
            user.ConcurrencyStamp ?? string.Empty,
            roles);
    }

    private async Task<Result<T, IdentityAdministrationError>> AuditAllowedFailureAsync<T>(
        IdentityAuditDescriptor audit,
        string? targetId,
        IdentityAdministrationError error)
    {
        dbContext.ChangeTracker.Clear();
        await auditFinalizer.FinalizeOnceAsync(
            audit with { TargetId = targetId ?? audit.TargetId },
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["result"] = "failed",
                ["errorCode"] = error.Code,
                ["message"] = error.Message,
            }).ConfigureAwait(false);
        return Result.Fail<T, IdentityAdministrationError>(error);
    }

    private static IdentityAdministrationError MapIdentityResult(IdentityResult result)
    {
        var code = result.Errors.Select(error => error.Code).FirstOrDefault() ?? "identity_failure";
        if (code.Contains("Concurrency", StringComparison.OrdinalIgnoreCase))
        {
            return new IdentityAdministrationError(
                "concurrency_conflict",
                "The account changed after it was loaded.");
        }

        if (code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return new IdentityAdministrationError(
                "duplicate_account",
                "The identity account already exists.");
        }

        if (code.Contains("Password", StringComparison.OrdinalIgnoreCase))
        {
            return new IdentityAdministrationError(
                "invalid_password",
                "The supplied password does not satisfy the configured policy.");
        }

        return new IdentityAdministrationError(
            "identity_failure",
            "The identity operation failed.");
    }

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };
}
