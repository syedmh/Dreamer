using WillVault.Domain.Entities;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Repository interface for <see cref="DeliveryJob"/> with queue-oriented queries.
/// </summary>
public interface IDeliveryJobRepository : IRepository<DeliveryJob>
{
    /// <summary>
    /// Retrieves delivery jobs that are pending and whose scheduled time has arrived.
    /// </summary>
    Task<IReadOnlyList<DeliveryJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all delivery jobs associated with a specific vault owner via their vault items.
    /// </summary>
    Task<IReadOnlyList<DeliveryJob>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves failed delivery jobs that are eligible for retry based on retry schedule and max attempts.
    /// </summary>
    Task<IReadOnlyList<DeliveryJob>> GetFailedJobsForRetryAsync(CancellationToken cancellationToken = default);
}
