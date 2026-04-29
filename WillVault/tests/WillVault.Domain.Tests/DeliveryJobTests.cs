using FluentAssertions;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Tests;

public class DeliveryJobTests
{
    [Fact]
    public void NewDeliveryJob_ShouldHavePendingStatus()
    {
        var job = new DeliveryJob();

        job.Status.Should().Be(DeliveryStatus.Pending);
        job.Attempts.Should().Be(0);
        job.MaxAttempts.Should().Be(5);
    }

    [Fact]
    public void RecordAttempt_Success_ShouldMarkDelivered()
    {
        var job = new DeliveryJob();

        job.RecordAttempt(success: true);

        job.Status.Should().Be(DeliveryStatus.Delivered);
        job.DeliveredAt.Should().NotBeNull();
        job.Attempts.Should().Be(1);
    }

    [Fact]
    public void RecordAttempt_Failure_ShouldScheduleRetry()
    {
        var job = new DeliveryJob();

        job.RecordAttempt(success: false, error: "Connection timeout");

        job.Status.Should().Be(DeliveryStatus.Failed);
        job.ErrorMessage.Should().Be("Connection timeout");
        job.Attempts.Should().Be(1);
        job.NextRetryAt.Should().NotBeNull();
        job.CanRetry.Should().BeTrue();
    }

    [Fact]
    public void RecordAttempt_ExhaustedRetries_ShouldMarkBounced()
    {
        var job = new DeliveryJob { MaxAttempts = 3 };

        job.RecordAttempt(success: false, error: "Error 1");
        job.RecordAttempt(success: false, error: "Error 2");
        job.RecordAttempt(success: false, error: "Error 3");

        job.Status.Should().Be(DeliveryStatus.Bounced);
        job.CanRetry.Should().BeFalse();
        job.Attempts.Should().Be(3);
    }

    [Fact]
    public void RecordAttempt_RetryBackoff_ShouldIncreaseExponentially()
    {
        var job = new DeliveryJob();
        var retryDelays = new List<DateTime?>();

        for (int i = 0; i < 4; i++)
        {
            job.RecordAttempt(success: false, error: $"Error {i + 1}");
            retryDelays.Add(job.NextRetryAt);
        }

        // Each retry should be further in the future than the last
        for (int i = 1; i < retryDelays.Count; i++)
        {
            retryDelays[i].Should().BeAfter(retryDelays[i - 1]!.Value);
        }
    }

    [Fact]
    public void RecordAttempt_SuccessAfterFailure_ShouldMarkDelivered()
    {
        var job = new DeliveryJob();

        job.RecordAttempt(success: false, error: "Temp error");
        job.Status.Should().Be(DeliveryStatus.Failed);

        // Reset status to pending for retry (simulating delivery engine)
        job.Status = DeliveryStatus.Pending;
        job.RecordAttempt(success: true);

        job.Status.Should().Be(DeliveryStatus.Delivered);
        job.Attempts.Should().Be(2);
    }
}
