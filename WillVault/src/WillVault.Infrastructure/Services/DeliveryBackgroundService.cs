using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Application.Interfaces.Services;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.Infrastructure.Services;

public class DeliveryBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeliveryBackgroundService> _logger;
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(60);

    public DeliveryBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<DeliveryBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeliveryBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDeliveryPipelineAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in delivery pipeline processing.");
            }

            await Task.Delay(PollingInterval, stoppingToken);
        }

        _logger.LogInformation("DeliveryBackgroundService stopped.");
    }

    private async Task ProcessDeliveryPipelineAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var ownerRepository = scope.ServiceProvider.GetRequiredService<IVaultOwnerRepository>();
        var verificationRepository = scope.ServiceProvider.GetRequiredService<IDeathVerificationRepository>();
        var vaultItemRepository = scope.ServiceProvider.GetRequiredService<IVaultItemRepository>();
        var deliveryJobRepository = scope.ServiceProvider.GetRequiredService<IDeliveryJobRepository>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

        // Step 1: Transition Verified owners with expired cooldowns to ReleaseScheduled
        await TransitionVerifiedToReleaseScheduledAsync(
            ownerRepository, verificationRepository, vaultItemRepository,
            deliveryJobRepository, auditService, cancellationToken);

        // Step 2: Transition ReleaseScheduled owners to Releasing
        await TransitionReleaseScheduledToReleasingAsync(
            ownerRepository, auditService, cancellationToken);

        // Step 3: Process pending delivery jobs
        await ProcessPendingJobsAsync(
            deliveryJobRepository, notificationService, auditService, cancellationToken);

        // Step 4: Retry failed jobs eligible for retry
        await RetryFailedJobsAsync(
            deliveryJobRepository, notificationService, auditService, cancellationToken);

        // Step 5: Close owners where all jobs are complete
        await CloseCompletedOwnersAsync(
            ownerRepository, deliveryJobRepository, auditService, cancellationToken);
    }

    private async Task TransitionVerifiedToReleaseScheduledAsync(
        IVaultOwnerRepository ownerRepository,
        IDeathVerificationRepository verificationRepository,
        IVaultItemRepository vaultItemRepository,
        IDeliveryJobRepository deliveryJobRepository,
        IAuditService auditService,
        CancellationToken cancellationToken)
    {
        var allOwners = await ownerRepository.GetAllAsync(cancellationToken);
        var verifiedOwners = allOwners.Where(o => o.AccountStatus == AccountStatus.Verified).ToList();

        foreach (var owner in verifiedOwners)
        {
            var requests = await verificationRepository.GetByOwnerIdAsync(owner.Id, cancellationToken);
            var approvedRequest = requests.FirstOrDefault(r => r.IsFullyApproved && r.IsCooldownExpired);

            if (approvedRequest is null) continue;

            _logger.LogInformation("Transitioning owner {OwnerId} to ReleaseScheduled.", owner.Id);

            owner.TransitionTo(AccountStatus.ReleaseScheduled);
            await ownerRepository.UpdateAsync(owner, cancellationToken);

            // Create delivery jobs for each VaultItem + Recipient combination
            var items = await vaultItemRepository.GetByOwnerIdAndTypeAsync(owner.Id, VaultItemType.Will, cancellationToken);
            var allItems = await vaultItemRepository.GetByOwnerIdAsync(owner.Id, 1, int.MaxValue, cancellationToken);

            foreach (var item in allItems.Items.Where(i => !i.IsArchived))
            {
                var itemWithRecipients = await vaultItemRepository.GetWithRecipientsAsync(item.Id, cancellationToken);
                if (itemWithRecipients is null) continue;

                foreach (var vir in itemWithRecipients.VaultItemRecipients)
                {
                    var scheduledAt = DateTime.UtcNow;
                    if (vir.ScheduledDeliveryDelay.HasValue)
                        scheduledAt = scheduledAt.Add(vir.ScheduledDeliveryDelay.Value);

                    // Create Email delivery job
                    var emailJob = new DeliveryJob
                    {
                        VaultItemId = item.Id,
                        RecipientId = vir.RecipientId,
                        Channel = DeliveryChannel.Email,
                        ScheduledAt = scheduledAt,
                        IdempotencyKey = $"{item.Id}-{vir.RecipientId}-{DeliveryChannel.Email}"
                    };
                    await deliveryJobRepository.AddAsync(emailJob, cancellationToken);

                    // Create SMS delivery job if recipient has a phone number
                    if (vir.Recipient?.Phone is not null)
                    {
                        var smsJob = new DeliveryJob
                        {
                            VaultItemId = item.Id,
                            RecipientId = vir.RecipientId,
                            Channel = DeliveryChannel.SMS,
                            ScheduledAt = scheduledAt,
                            IdempotencyKey = $"{item.Id}-{vir.RecipientId}-{DeliveryChannel.SMS}"
                        };
                        await deliveryJobRepository.AddAsync(smsJob, cancellationToken);
                    }
                }
            }

            await auditService.LogAsync(
                null, ActorType.System, "OwnerTransitionedToReleaseScheduled",
                "VaultOwner", owner.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task TransitionReleaseScheduledToReleasingAsync(
        IVaultOwnerRepository ownerRepository,
        IAuditService auditService,
        CancellationToken cancellationToken)
    {
        var allOwners = await ownerRepository.GetAllAsync(cancellationToken);
        var scheduledOwners = allOwners.Where(o => o.AccountStatus == AccountStatus.ReleaseScheduled).ToList();

        foreach (var owner in scheduledOwners)
        {
            _logger.LogInformation("Transitioning owner {OwnerId} to Releasing.", owner.Id);

            owner.TransitionTo(AccountStatus.Releasing);
            await ownerRepository.UpdateAsync(owner, cancellationToken);

            await auditService.LogAsync(
                null, ActorType.System, "OwnerTransitionedToReleasing",
                "VaultOwner", owner.Id, cancellationToken: cancellationToken);
        }
    }

    private async Task ProcessPendingJobsAsync(
        IDeliveryJobRepository deliveryJobRepository,
        INotificationService notificationService,
        IAuditService auditService,
        CancellationToken cancellationToken)
    {
        var pendingJobs = await deliveryJobRepository.GetPendingJobsAsync(cancellationToken);

        foreach (var job in pendingJobs)
        {
            try
            {
                await SendNotificationAsync(job, notificationService, cancellationToken);
                job.RecordAttempt(success: true);
                _logger.LogInformation("Delivery job {JobId} succeeded.", job.Id);
            }
            catch (Exception ex)
            {
                job.RecordAttempt(success: false, error: ex.Message);
                _logger.LogWarning(ex, "Delivery job {JobId} failed on attempt {Attempt}.", job.Id, job.Attempts);
            }

            await deliveryJobRepository.UpdateAsync(job, cancellationToken);

            await auditService.LogAsync(
                null, ActorType.System,
                job.Status == DeliveryStatus.Delivered ? "DeliveryJobCompleted" : "DeliveryJobAttemptFailed",
                "DeliveryJob", job.Id,
                details: $"{{\"attempt\":{job.Attempts},\"status\":\"{job.Status}\"}}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task RetryFailedJobsAsync(
        IDeliveryJobRepository deliveryJobRepository,
        INotificationService notificationService,
        IAuditService auditService,
        CancellationToken cancellationToken)
    {
        var retryJobs = await deliveryJobRepository.GetFailedJobsForRetryAsync(cancellationToken);

        foreach (var job in retryJobs)
        {
            try
            {
                await SendNotificationAsync(job, notificationService, cancellationToken);
                job.RecordAttempt(success: true);
                _logger.LogInformation("Retry delivery job {JobId} succeeded.", job.Id);
            }
            catch (Exception ex)
            {
                job.RecordAttempt(success: false, error: ex.Message);
                _logger.LogWarning(ex, "Retry delivery job {JobId} failed on attempt {Attempt}.", job.Id, job.Attempts);
            }

            await deliveryJobRepository.UpdateAsync(job, cancellationToken);

            await auditService.LogAsync(
                null, ActorType.System,
                job.Status == DeliveryStatus.Delivered ? "DeliveryJobRetryCompleted" : "DeliveryJobRetryFailed",
                "DeliveryJob", job.Id,
                cancellationToken: cancellationToken);
        }
    }

    private async Task CloseCompletedOwnersAsync(
        IVaultOwnerRepository ownerRepository,
        IDeliveryJobRepository deliveryJobRepository,
        IAuditService auditService,
        CancellationToken cancellationToken)
    {
        var allOwners = await ownerRepository.GetAllAsync(cancellationToken);
        var releasingOwners = allOwners.Where(o => o.AccountStatus == AccountStatus.Releasing).ToList();

        foreach (var owner in releasingOwners)
        {
            var jobs = await deliveryJobRepository.GetByOwnerIdAsync(owner.Id, cancellationToken);

            if (jobs.Count == 0) continue;

            var allComplete = jobs.All(j =>
                j.Status == DeliveryStatus.Delivered || j.Status == DeliveryStatus.Bounced);

            if (!allComplete) continue;

            _logger.LogInformation("All delivery jobs complete for owner {OwnerId}. Transitioning to Closed.", owner.Id);

            owner.TransitionTo(AccountStatus.Closed);
            await ownerRepository.UpdateAsync(owner, cancellationToken);

            await auditService.LogAsync(
                null, ActorType.System, "OwnerTransitionedToClosed",
                "VaultOwner", owner.Id, cancellationToken: cancellationToken);
        }
    }

    private static async Task SendNotificationAsync(
        DeliveryJob job,
        INotificationService notificationService,
        CancellationToken cancellationToken)
    {
        var itemTitle = job.VaultItem?.Title ?? "Vault Item";

        switch (job.Channel)
        {
            case DeliveryChannel.Email:
                var recipientEmail = job.Recipient?.Email ?? throw new InvalidOperationException("Recipient email not available.");
                await notificationService.SendEmailAsync(
                    recipientEmail,
                    $"WillVault: You have received \"{itemTitle}\"",
                    $"A vault item titled \"{itemTitle}\" has been delivered to you through WillVault.",
                    cancellationToken: cancellationToken);
                break;

            case DeliveryChannel.SMS:
                var recipientPhone = job.Recipient?.Phone ?? throw new InvalidOperationException("Recipient phone not available.");
                await notificationService.SendSmsAsync(
                    recipientPhone,
                    $"WillVault: You have received \"{itemTitle}\". Please check your email for details.",
                    cancellationToken);
                break;

            default:
                throw new NotSupportedException($"Delivery channel {job.Channel} is not supported.");
        }
    }
}
