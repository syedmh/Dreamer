using System.Reflection;
using HusayniaTabruk.Application.Abstractions.Persistence;

namespace HusayniaTabruk.Application.Tests.Dates.Management;

public sealed class T17DateManagementContractTests
{
    [Fact]
    public void CommandsAreIdentifierScalarOnlyAndVersionRemainsAnExplicitMethodParameter()
    {
        Type service = RequireType("DateManagementService");
        Type editDate = RequireType("EditServiceDateCommand");
        Type editNeed = RequireType("EditHelpNeedCommand");
        Type closeDate = RequireType("CloseServiceDateCommand");
        Type cancelDate = RequireType("CancelServiceDateCommand");

        Assert.Equal(
            ["ServiceDateId", "Title", "Instructions", "StartsAt", "EndsAt", "CancellationDeadlineAt"],
            editDate.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["HelpNeedId", "Instructions", "Capacity", "Status"],
            editNeed.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["ServiceDateId", "Reason", "IdempotencyKey"],
            closeDate.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(
            ["ServiceDateId", "Reason", "IdempotencyKey"],
            cancelDate.GetProperties().Select(property => property.Name).ToArray());

        AssertVersionParameter(service, "EditDateAsync", "expectedDateVersion");
        AssertVersionParameter(service, "EditNeedAsync", "expectedNeedVersion");
        AssertVersionParameter(service, "CloseAsync", "expectedDateVersion");
        AssertVersionParameter(service, "CancelAsync", "expectedDateVersion");

        string[] forbidden =
        [
            "Actor",
            "Tenant",
            "Organization",
            "Membership",
            "ManagerMembership",
            "Category",
            "Aggregate",
            "Signups",
            "Thread",
            "Clock",
            "Timestamp",
            "Version",
            "IfMatch",
            "Role",
            "Current",
        ];
        Assert.DoesNotContain(
            editDate.GetProperties()
                .Concat(editNeed.GetProperties())
                .Concat(closeDate.GetProperties())
                .Concat(cancelDate.GetProperties()),
            property => forbidden.Any(
                fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertVersionParameter(
        Type service,
        string methodName,
        string versionParameterName)
    {
        MethodInfo method = service.GetMethod(methodName)
            ?? throw new InvalidOperationException($"Missing method '{methodName}'.");
        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(versionParameterName, parameters[1].Name);
        Assert.Equal(typeof(long), parameters[1].ParameterType);
    }

    private static Type RequireType(string name)
    {
        Type? type = typeof(IUnitOfWork).Assembly.GetTypes()
            .SingleOrDefault(candidate => candidate.Name == name);
        Assert.NotNull(type);
        return type!;
    }
}
