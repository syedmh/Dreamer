using System.Reflection;
using HusayniaTabruk.Application.Signups.Submit;

namespace HusayniaTabruk.Application.Tests.Signups.Cancellation;

public sealed class T16CancellationContractTests
{
    [Fact]
    public void CommandsAreIdentifierOnlyAndDoNotAcceptCallerDateDeadlineOrAggregateContext()
    {
        Type withdraw = Require("WithdrawSignupCommand");
        Type overrideCancellation = Require("OverrideSignupCancellationCommand");
        Type reassign = Require("ReassignWaitlistedSignupCommand");

        Assert.Equal(
            ["SignupId", "IdempotencyKey"],
            withdraw.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["SignupId", "TargetStatus", "Reason", "IdempotencyKey"],
            overrideCancellation.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["HelpNeedId", "SignupId", "Reason", "IdempotencyKey"],
            reassign.GetProperties().Select(property => property.Name).ToArray());

        string[] forbidden =
        [
            "Actor", "Tenant", "Organization", "Membership", "Date", "Deadline",
            "Timestamp", "Clock", "Aggregate", "SignupVersion", "WaitlistOrder",
            "Capacity", "Signups",
        ];
        Assert.DoesNotContain(
            withdraw.GetProperties()
                .Concat(overrideCancellation.GetProperties())
                .Concat(reassign.GetProperties()),
            property => forbidden.Any(
                fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static Type Require(string name) =>
        typeof(SubmitSignupCommand).Assembly.GetTypes().Single(type => type.Name == name);
}
