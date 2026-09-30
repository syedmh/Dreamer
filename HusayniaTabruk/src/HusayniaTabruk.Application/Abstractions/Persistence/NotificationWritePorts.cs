using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public interface INotificationWriter
{
    ValueTask AddAsync(
        Notification notification,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This notification writer does not support persistence writes.");
}
