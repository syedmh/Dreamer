using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Architecture;

public sealed class AdminMutationContractTests
{
    [Fact]
    public void GovernanceMutationCommandsExposeOnlyTypedIdentifiersEnumActionsAndReasons()
    {
        AssertShape<AssignFoodInchargeCommand>(
            (nameof(AssignFoodInchargeCommand.TargetMembershipId), typeof(MembershipId)),
            (nameof(AssignFoodInchargeCommand.Reason), typeof(string)));
        AssertShape<RevokeFoodInchargeCommand>(
            (nameof(RevokeFoodInchargeCommand.TargetMembershipId), typeof(MembershipId)),
            (nameof(RevokeFoodInchargeCommand.Reason), typeof(string)));
        AssertShape<ProposeAdministratorRoleChangeCommand>(
            (nameof(ProposeAdministratorRoleChangeCommand.TargetMembershipId), typeof(MembershipId)),
            (nameof(ProposeAdministratorRoleChangeCommand.Action), typeof(AdministratorRoleChangeAction)),
            (nameof(ProposeAdministratorRoleChangeCommand.Reason), typeof(string)));
        AssertShape<ApproveAdministratorRoleChangeCommand>(
            (nameof(ApproveAdministratorRoleChangeCommand.RequestId), typeof(RoleChangeRequestId)),
            (nameof(ApproveAdministratorRoleChangeCommand.Reason), typeof(string)));
        AssertShape<DisableMembershipCommand>(
            (nameof(DisableMembershipCommand.TargetMembershipId), typeof(MembershipId)),
            (nameof(DisableMembershipCommand.Reason), typeof(string)));
        AssertShape<BootstrapAdministratorsCommand>(
            (nameof(BootstrapAdministratorsCommand.OrganizationId), typeof(OrganizationId)),
            (nameof(BootstrapAdministratorsCommand.AdministratorMembershipIds), typeof(IReadOnlyCollection<MembershipId>)),
            (nameof(BootstrapAdministratorsCommand.Reason), typeof(string)));
    }

    private static void AssertShape<TCommand>(params (string Name, Type Type)[] expected)
    {
        Type commandType = typeof(TCommand);
        Assert.Equal(expected.Select(item => item.Name).ToArray(), commandType.GetProperties().Select(property => property.Name).ToArray());
        Assert.Equal(expected.Select(item => item.Type).ToArray(), commandType.GetProperties().Select(property => property.PropertyType).ToArray());
    }
}
