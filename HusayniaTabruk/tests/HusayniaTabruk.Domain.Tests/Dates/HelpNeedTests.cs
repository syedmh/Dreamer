using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;

namespace HusayniaTabruk.Domain.Tests.Dates;

public sealed class HelpNeedTests
{
    [Fact]
    public void ChangeNeedUpdatesOwnedInstructionsCapacityAndStatusOnce()
    {
        ServiceDate date = CreateDate();
        HelpNeed need = Assert.Single(date.HelpNeeds);

        Result<HelpNeedChanged> result = date.ChangeNeed(
            need.Id,
            "Updated instructions",
            null,
            HelpNeedStatus.Closed,
            UtcNow);

        Assert.True(result.IsSuccess);
        HelpNeed changed = Assert.Single(date.HelpNeeds);
        Assert.Equal("Updated instructions", changed.Instructions);
        Assert.Null(changed.Capacity);
        Assert.Equal(HelpNeedStatus.Closed, changed.Status);
        Assert.Equal(1, changed.Version);
        Assert.Equal(1, date.Version);
    }

    [Fact]
    public void ChangeNeedCanReopenAClosedCategoryOnMutableParent()
    {
        ServiceDate date = CreateDate(status: HelpNeedStatus.Closed, needVersion: 1);
        HelpNeed need = Assert.Single(date.HelpNeeds);

        Result<HelpNeedChanged> result = date.ChangeNeed(
            need.Id,
            need.Instructions,
            need.Capacity,
            HelpNeedStatus.Open,
            UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(HelpNeedStatus.Open, Assert.Single(date.HelpNeeds).Status);
    }

    [Fact]
    public void ChangeNeedRejectsNoOpWithoutIncrementingVersions()
    {
        ServiceDate date = CreateDate();
        HelpNeed need = Assert.Single(date.HelpNeeds);

        Result<HelpNeedChanged> result = date.ChangeNeed(
            need.Id,
            need.Instructions,
            need.Capacity,
            need.Status,
            UtcNow);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
        Assert.Equal(0, Assert.Single(date.HelpNeeds).Version);
        Assert.Equal(1, date.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ChangeNeedRejectsBlankInstructionsWithoutMutation(string instructions)
    {
        ServiceDate date = CreateDate();
        HelpNeed need = Assert.Single(date.HelpNeeds);

        Result<HelpNeedChanged> result = date.ChangeNeed(
            need.Id,
            instructions,
            10,
            HelpNeedStatus.Closed,
            UtcNow);

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal("Instructions", retained.Instructions);
        Assert.Equal(5, retained.Capacity);
        Assert.Equal(HelpNeedStatus.Open, retained.Status);
        Assert.Equal(0, retained.Version);
        Assert.Equal(1, date.Version);
    }

    [Fact]
    public void RehydrateRejectsUndefinedEnumsNegativeCapacityAndNegativeVersion()
    {
        AssertInvalid(category: (HelpCategory)999);
        AssertInvalid(status: (HelpNeedStatus)999);
        AssertInvalid(capacity: -1);
        AssertInvalid(version: -1);
    }

    [Fact]
    public void ChangeNeedRejectsNonUtcBeforeMutation()
    {
        ServiceDate date = CreateDate();
        HelpNeed need = Assert.Single(date.HelpNeeds);

        Assert.Throws<ArgumentException>(
            () => date.ChangeNeed(
                need.Id,
                "Changed",
                null,
                HelpNeedStatus.Closed,
                UtcNow.ToOffset(TimeSpan.FromHours(1))));
        Assert.Equal("Instructions", Assert.Single(date.HelpNeeds).Instructions);
        Assert.Equal(0, Assert.Single(date.HelpNeeds).Version);
        Assert.Equal(1, date.Version);
    }

    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static ServiceDate CreateDate(
        HelpNeedStatus status = HelpNeedStatus.Open,
        long needVersion = 0)
    {
        ServiceDateId dateId = ServiceDateId.New();
        HelpNeed need = HelpNeed.Rehydrate(
            HelpNeedId.New(),
            dateId,
            HelpCategory.Serving,
            "Instructions",
            5,
            status,
            needVersion).Value;
        return ServiceDate.Rehydrate(
            dateId,
            OrganizationId.New(),
            "Service date",
            "Instructions",
            UtcNow.AddDays(1),
            UtcNow.AddDays(2),
            UtcNow,
            MembershipId.New(),
            ServiceDateStatus.Draft,
            1,
            [need]).Value;
    }

    private static void AssertInvalid(
        HelpCategory category = HelpCategory.Serving,
        HelpNeedStatus status = HelpNeedStatus.Open,
        int? capacity = 5,
        long version = 0)
    {
        Result<HelpNeed> result = HelpNeed.Rehydrate(
            HelpNeedId.New(),
            ServiceDateId.New(),
            category,
            "Instructions",
            capacity,
            status,
            version);

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
    }
}
