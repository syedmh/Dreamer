using FluentAssertions;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Tests;

public class DeathVerificationRequestTests
{
    [Fact]
    public void NewRequest_ShouldHavePendingStatus()
    {
        var request = new DeathVerificationRequest();

        request.Status.Should().Be(VerificationStatus.Pending);
        request.RequiresSecondApproval.Should().BeTrue();
    }

    [Fact]
    public void IsFullyApproved_WithoutSecondApproval_WhenNotRequired_ShouldBeTrue()
    {
        var request = new DeathVerificationRequest
        {
            Status = VerificationStatus.Approved,
            ReviewedById = Guid.NewGuid(),
            RequiresSecondApproval = false
        };

        request.IsFullyApproved.Should().BeTrue();
    }

    [Fact]
    public void IsFullyApproved_WithDualApproval_ShouldRequireBothReviewers()
    {
        var request = new DeathVerificationRequest
        {
            Status = VerificationStatus.Approved,
            ReviewedById = Guid.NewGuid(),
            RequiresSecondApproval = true,
            SecondReviewerId = null
        };

        request.IsFullyApproved.Should().BeFalse();

        request.SecondReviewerId = Guid.NewGuid();
        request.IsFullyApproved.Should().BeTrue();
    }

    [Fact]
    public void IsCooldownExpired_BeforeExpiry_ShouldBeFalse()
    {
        var request = new DeathVerificationRequest
        {
            CooldownExpiresAt = DateTime.UtcNow.AddHours(24)
        };

        request.IsCooldownExpired.Should().BeFalse();
    }

    [Fact]
    public void IsCooldownExpired_AfterExpiry_ShouldBeTrue()
    {
        var request = new DeathVerificationRequest
        {
            CooldownExpiresAt = DateTime.UtcNow.AddHours(-1)
        };

        request.IsCooldownExpired.Should().BeTrue();
    }

    [Fact]
    public void IsCooldownExpired_WhenNotSet_ShouldBeFalse()
    {
        var request = new DeathVerificationRequest
        {
            CooldownExpiresAt = null
        };

        request.IsCooldownExpired.Should().BeFalse();
    }
}
