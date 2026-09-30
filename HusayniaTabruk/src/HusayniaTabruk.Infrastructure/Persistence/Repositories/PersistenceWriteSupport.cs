using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

public sealed record PersistenceEffects(
    IReadOnlyCollection<NotificationEntity> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<PrivilegedAccessEntry> PrivilegedAccessEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages)
{
    public static PersistenceEffects Empty { get; } =
        new([], [], [], []);
}

internal static class PersistenceWriteSupport
{
    public static bool IsEmpty(PersistenceEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        return effects.Notifications.Count == 0
            && effects.AuditEntries.Count == 0
            && effects.PrivilegedAccessEntries.Count == 0
            && effects.OutboxMessages.Count == 0;
    }

    public static bool IsEmpty(ThreadPersistenceEffects effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        return effects.Notifications.Count == 0
            && effects.AuditEntries.Count == 0
            && effects.PrivilegedAccessEntries.Count == 0
            && effects.OutboxMessages.Count == 0
            && effects.ModerationEvents.Count == 0;
    }

    public static void AddEffects(TabrukDbContext context, PersistenceEffects effects)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(effects.Notifications);
        ArgumentNullException.ThrowIfNull(effects.AuditEntries);
        ArgumentNullException.ThrowIfNull(effects.PrivilegedAccessEntries);
        ArgumentNullException.ThrowIfNull(effects.OutboxMessages);

        context.Notifications.AddRange(effects.Notifications);
        context.AuditEvents.AddRange(effects.AuditEntries.Select(ToEntity));
        context.PrivilegedAccessEvents.AddRange(effects.PrivilegedAccessEntries.Select(ToEntity));
        context.OutboxMessages.AddRange(effects.OutboxMessages.Select(ToEntity));
    }

    public static AuditEventEntity ToEntity(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new AuditEventEntity
        {
            Id = entry.Id.Value,
            OrganizationId = entry.OrganizationId.Value,
            ActorMembershipId = entry.ActorMembershipId.Value,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Reason = entry.Reason,
            Purpose = entry.Purpose,
            CorrelationId = entry.CorrelationId,
            BeforeState = entry.BeforeState,
            AfterState = entry.AfterState,
            OccurredAt = entry.OccurredAt,
        };
    }

    public static PrivilegedAccessEventEntity ToEntity(PrivilegedAccessEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new PrivilegedAccessEventEntity
        {
            Id = entry.Id.Value,
            OrganizationId = entry.OrganizationId.Value,
            ActorMembershipId = entry.ActorMembershipId.Value,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Reason = entry.Reason,
            Purpose = checked((short)entry.Purpose),
            CaseId = entry.CaseId,
            PageCursor = entry.PageCursor,
            PageHash = entry.PageHash.Value,
            OccurredAt = entry.OccurredAt,
        };
    }

    public static OutboxMessageEntity ToEntity(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new OutboxMessageEntity
        {
            Id = message.Id.Value,
            OrganizationId = message.OrganizationId.Value,
            Type = message.Type,
            Payload = message.Payload,
            Attempts = 0,
            NextAttemptAt = message.OccurredAt,
            OccurredAt = message.OccurredAt,
        };
    }

    public static NotificationEntity ToEntity(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return new NotificationEntity
        {
            Id = notification.Id.Value,
            OrganizationId = notification.OrganizationId.Value,
            RecipientMembershipId = notification.RecipientMembershipId.Value,
            Type = checked((short)notification.Type),
            ResourceType = checked((short)notification.ResourceType),
            ResourceId = notification.ResourceId,
            Title = notification.Title,
            Body = notification.Body,
            CreatedAt = notification.CreatedAt,
            ReadAt = notification.ReadAt,
        };
    }

    public static ValueTask<Result<T>> ExecuteWriteAsync<T>(
        TabrukDbContext context,
        Func<CancellationToken, ValueTask<Result<T>>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(write);

        return PostgresDependencyFailure.ExecuteAsync(
            () => ExecuteWriteCoreAsync(context, write, cancellationToken));
    }

    private static async ValueTask<Result<T>> ExecuteWriteCoreAsync<T>(
        TabrukDbContext context,
        Func<CancellationToken, ValueTask<Result<T>>> write,
        CancellationToken cancellationToken)
    {
        bool ownsTransaction = context.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;
        Exception? primaryException = null;
        bool rollbackOwnedTransaction = false;
        bool clearChangeTracker = false;

        try
        {
            Result<T> result = await write(cancellationToken);
            if (result.IsFailure)
            {
                rollbackOwnedTransaction = ownsTransaction;
                clearChangeTracker = true;
                return result;
            }

            await context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            rollbackOwnedTransaction = ownsTransaction;
            clearChangeTracker = true;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction,
                clearChangeTracker,
                primaryException);
        }
    }

    public static async ValueTask<Result> ExecuteWriteAsync(
        TabrukDbContext context,
        Func<CancellationToken, ValueTask<Result>> write,
        CancellationToken cancellationToken)
    {
        Result<bool> result = await ExecuteWriteAsync(
            context,
            async token =>
            {
                Result writeResult = await write(token);
                return writeResult.IsSuccess
                    ? Result.Success(true)
                    : Result.Failure<bool>(writeResult.Error);
            },
            cancellationToken);

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }
}
