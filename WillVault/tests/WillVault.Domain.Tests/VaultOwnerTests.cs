using FluentAssertions;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.Domain.Tests;

public class VaultOwnerTests
{
    [Fact]
    public void NewVaultOwner_ShouldHaveActiveStatus()
    {
        var owner = new VaultOwner { FullName = "Test User", Email = "test@example.com" };

        owner.AccountStatus.Should().Be(AccountStatus.Active);
        owner.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(AccountStatus.Active, AccountStatus.VerificationPending, true)]
    [InlineData(AccountStatus.VerificationPending, AccountStatus.Verified, true)]
    [InlineData(AccountStatus.VerificationPending, AccountStatus.Active, true)]
    [InlineData(AccountStatus.Verified, AccountStatus.ReleaseScheduled, true)]
    [InlineData(AccountStatus.Verified, AccountStatus.VerificationPending, true)]
    [InlineData(AccountStatus.ReleaseScheduled, AccountStatus.Releasing, true)]
    [InlineData(AccountStatus.ReleaseScheduled, AccountStatus.VerificationPending, true)]
    [InlineData(AccountStatus.Releasing, AccountStatus.Closed, true)]
    [InlineData(AccountStatus.Active, AccountStatus.Closed, false)]
    [InlineData(AccountStatus.Active, AccountStatus.Releasing, false)]
    [InlineData(AccountStatus.Closed, AccountStatus.Active, false)]
    [InlineData(AccountStatus.VerificationPending, AccountStatus.Closed, false)]
    [InlineData(AccountStatus.Releasing, AccountStatus.Active, false)]
    public void CanTransitionTo_ShouldValidateStateTransitions(
        AccountStatus from, AccountStatus to, bool expected)
    {
        var owner = new VaultOwner { AccountStatus = from };

        owner.CanTransitionTo(to).Should().Be(expected);
    }

    [Fact]
    public void TransitionTo_ValidTransition_ShouldUpdateStatus()
    {
        var owner = new VaultOwner { AccountStatus = AccountStatus.Active };

        owner.TransitionTo(AccountStatus.VerificationPending);

        owner.AccountStatus.Should().Be(AccountStatus.VerificationPending);
        owner.IsActive.Should().BeFalse();
    }

    [Fact]
    public void TransitionTo_InvalidTransition_ShouldThrow()
    {
        var owner = new VaultOwner { AccountStatus = AccountStatus.Active };

        var act = () => owner.TransitionTo(AccountStatus.Closed);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot transition*Active*Closed*");
    }

    [Fact]
    public void FullLifecycle_ShouldFollowStateMachine()
    {
        var owner = new VaultOwner
        {
            FullName = "John Doe",
            Email = "john@example.com",
            AccountStatus = AccountStatus.Active
        };

        owner.TransitionTo(AccountStatus.VerificationPending);
        owner.TransitionTo(AccountStatus.Verified);
        owner.TransitionTo(AccountStatus.ReleaseScheduled);
        owner.TransitionTo(AccountStatus.Releasing);
        owner.TransitionTo(AccountStatus.Closed);

        owner.AccountStatus.Should().Be(AccountStatus.Closed);
    }

    [Fact]
    public void DisputeFlow_ShouldAllowReReview()
    {
        var owner = new VaultOwner { AccountStatus = AccountStatus.Verified };

        // Dispute sends back to VerificationPending
        owner.TransitionTo(AccountStatus.VerificationPending);
        owner.AccountStatus.Should().Be(AccountStatus.VerificationPending);

        // Can be re-approved
        owner.TransitionTo(AccountStatus.Verified);
        owner.AccountStatus.Should().Be(AccountStatus.Verified);
    }
}
