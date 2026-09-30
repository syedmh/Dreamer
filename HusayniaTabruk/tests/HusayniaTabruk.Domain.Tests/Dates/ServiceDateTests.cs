using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;

namespace HusayniaTabruk.Domain.Tests.Dates;

public sealed class ServiceDateTests
{
    [Fact]
    public void CreateBuildsDraftWithFrozenFieldsAndNoNeeds()
    {
        Result<ServiceDate> result = Create();

        Assert.True(result.IsSuccess);
        Assert.Equal(ServiceDateStatus.Draft, result.Value.Status);
        Assert.Equal("Service day", result.Value.Title);
        Assert.Equal(StartsAt, result.Value.StartsAt);
        Assert.Equal(StartsAt, result.Value.CancellationDeadlineAt);
        Assert.Empty(result.Value.HelpNeeds);
        Assert.Equal(0, result.Value.Version);
    }

    [Theory]
    [InlineData("", "Instructions")]
    [InlineData("Title", " ")]
    public void CreateRejectsBlankRequiredText(string title, string instructions)
    {
        Result<ServiceDate> result = Create(title: title, instructions: instructions);

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
    }

    [Fact]
    public void CreateRequiresStartBeforeEnd()
    {
        Result<ServiceDate> equal = Create(startsAt: StartsAt, endsAt: StartsAt);
        Result<ServiceDate> reversed = Create(startsAt: StartsAt, endsAt: StartsAt.AddTicks(-1));

        Assert.Equal(DateErrorCodes.InvalidDateInput, equal.Error.Code);
        Assert.Equal(DateErrorCodes.InvalidDateInput, reversed.Error.Code);
    }

    [Fact]
    public void CancellationDeadlineMayEqualStartButCannotFollowIt()
    {
        Assert.True(Create(cancellationDeadlineAt: StartsAt).IsSuccess);

        Result<ServiceDate> afterStart =
            Create(cancellationDeadlineAt: StartsAt.AddTicks(1));

        Assert.True(afterStart.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, afterStart.Error.Code);
    }

    [Fact]
    public void DomainTimestampsMustBeUtc()
    {
        DateTimeOffset localStart = StartsAt.ToOffset(TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => Create(startsAt: localStart));

        ServiceDate date = Create().Value;
        Assert.Throws<ArgumentException>(() => date.Open(localStart.AddHours(-1)));
        Assert.Equal(ServiceDateStatus.Draft, date.Status);
        Assert.Equal(0, date.Version);
    }

    [Fact]
    public void AddNeedSupportsFiniteAndUnlimitedCapacity()
    {
        ServiceDate date = Create().Value;

        Result<HelpNeedAdded> finite = date.AddNeed(
            HelpNeedId.New(),
            HelpCategory.FoodPreparation,
            "Prepare food",
            1,
            BeforeStart);
        Result<HelpNeedAdded> unlimited = date.AddNeed(
            HelpNeedId.New(),
            HelpCategory.Serving,
            "Serve food",
            null,
            BeforeStart);

        Assert.True(finite.IsSuccess);
        Assert.Equal(1, finite.Value.HelpNeed.Capacity);
        Assert.True(unlimited.IsSuccess);
        Assert.Null(unlimited.Value.HelpNeed.Capacity);
        Assert.Equal(2, date.HelpNeeds.Count);
        Assert.Equal(2, date.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddNeedRejectsNonPositiveCapacityWithoutMutation(int capacity)
    {
        ServiceDate date = Create().Value;

        Result<HelpNeedAdded> result = date.AddNeed(
            HelpNeedId.New(),
            HelpCategory.Cleanup,
            "Clean up",
            capacity,
            BeforeStart);

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
        Assert.Empty(date.HelpNeeds);
        Assert.Equal(0, date.Version);
    }

    [Fact]
    public void AddNeedRejectsDuplicateCategoryWithoutReplacingHistory()
    {
        ServiceDate date = Create().Value;
        Assert.True(
            date.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Serving,
                "Initial",
                4,
                BeforeStart).IsSuccess);
        HelpNeed original = Assert.Single(date.HelpNeeds);

        Result<HelpNeedAdded> duplicate = date.AddNeed(
            HelpNeedId.New(),
            HelpCategory.Serving,
            "Replacement",
            8,
            BeforeStart);

        Assert.True(duplicate.IsFailure);
        Assert.Equal(DateErrorCodes.DuplicateHelpCategory, duplicate.Error.Code);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal("Initial", retained.Instructions);
        Assert.Equal(1, date.Version);
    }

    [Fact]
    public void OpenSucceedsOneTickBeforeStartAndFailsAtStart()
    {
        ServiceDate beforeBoundary = Create().Value;
        ServiceDate atBoundary = Create().Value;

        Assert.True(beforeBoundary.Open(StartsAt.AddTicks(-1)).IsSuccess);
        Result<ServiceDateOpened> result = atBoundary.Open(StartsAt);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
        Assert.Equal(ServiceDateStatus.Draft, atBoundary.Status);
    }

    [Fact]
    public void InvalidOpenAndCloseTransitionsPreserveStateAndNeeds()
    {
        ServiceDate date = CreateWithNeed();
        long version = date.Version;

        Assert.True(date.Close(BeforeStart).IsFailure);
        Assert.Equal(ServiceDateStatus.Draft, date.Status);
        Assert.Equal(HelpNeedStatus.Open, Assert.Single(date.HelpNeeds).Status);
        Assert.Equal(version, date.Version);

        Assert.True(date.Open(BeforeStart).IsSuccess);
        long openVersion = date.Version;
        Assert.True(date.Open(BeforeStart).IsFailure);
        Assert.Equal(ServiceDateStatus.Open, date.Status);
        Assert.Equal(openVersion, date.Version);
    }

    [Fact]
    public void CloseRetainsNeedsAndClosesEveryCategory()
    {
        ServiceDate date = CreateWithNeed();
        HelpNeedId needId = Assert.Single(date.HelpNeeds).Id;
        Assert.True(date.Open(BeforeStart).IsSuccess);

        Result<ServiceDateClosed> result = date.Close(StartsAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(ServiceDateStatus.Closed, date.Status);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal(needId, retained.Id);
        Assert.Equal(HelpNeedStatus.Closed, retained.Status);
        Assert.Contains(needId, result.Value.RetainedHelpNeedIds);
        Assert.Equal(3, date.Version);
    }

    [Theory]
    [InlineData(ServiceDateStatus.Draft)]
    [InlineData(ServiceDateStatus.Open)]
    [InlineData(ServiceDateStatus.Closed)]
    public void CancelRetainsNeedsAndPreventsFurtherChanges(ServiceDateStatus initialStatus)
    {
        ServiceDate date = CreateWithNeed();
        if (initialStatus is ServiceDateStatus.Open or ServiceDateStatus.Closed)
        {
            Assert.True(date.Open(BeforeStart).IsSuccess);
        }

        if (initialStatus == ServiceDateStatus.Closed)
        {
            Assert.True(date.Close(BeforeStart).IsSuccess);
        }

        HelpNeedId needId = Assert.Single(date.HelpNeeds).Id;
        Result<ServiceDateCancelled> cancelled = date.Cancel(StartsAt);

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(ServiceDateStatus.Cancelled, date.Status);
        Assert.Equal(needId, Assert.Single(date.HelpNeeds).Id);
        Assert.Equal(HelpNeedStatus.Closed, Assert.Single(date.HelpNeeds).Status);

        long cancelledVersion = date.Version;
        Assert.True(date.Cancel(StartsAt.AddTicks(1)).IsFailure);
        Assert.True(
            date.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Cleanup,
                "Cleanup",
                null,
                StartsAt).IsFailure);
        Assert.Equal(cancelledVersion, date.Version);
        Assert.Single(date.HelpNeeds);
    }

    [Fact]
    public void AddNeedRejectsAtExactEndBoundary()
    {
        ServiceDate date = Create().Value;

        Result<HelpNeedAdded> result = date.AddNeed(
            HelpNeedId.New(),
            HelpCategory.Cleanup,
            "Cleanup",
            null,
            EndsAt);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
        Assert.Empty(date.HelpNeeds);
    }

    [Fact]
    public void RehydrateRejectsDuplicateOrCrossDateNeedHistory()
    {
        ServiceDateId dateId = ServiceDateId.New();
        HelpNeed need = HelpNeed.Rehydrate(
            HelpNeedId.New(),
            ServiceDateId.New(),
            HelpCategory.Cleanup,
            "Cleanup",
            null,
            HelpNeedStatus.Open,
            0).Value;

        Result<ServiceDate> result = Rehydrate(dateId, [need]);

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
    }

    private static readonly DateTimeOffset StartsAt =
        new(2026, 8, 20, 17, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EndsAt = StartsAt.AddHours(4);
    private static readonly DateTimeOffset BeforeStart = StartsAt.AddDays(-1);

    private static Result<ServiceDate> Create(
        string title = "Service day",
        string instructions = "Please arrive on time.",
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        DateTimeOffset? cancellationDeadlineAt = null) =>
        ServiceDate.Create(
            ServiceDateId.New(),
            OrganizationId.New(),
            title,
            instructions,
            startsAt ?? StartsAt,
            endsAt ?? EndsAt,
            cancellationDeadlineAt ?? StartsAt,
            MembershipId.New());

    private static ServiceDate CreateWithNeed()
    {
        ServiceDate date = Create().Value;
        Assert.True(
            date.AddNeed(
                HelpNeedId.New(),
                HelpCategory.FoodPreparation,
                "Prepare food",
                5,
                BeforeStart).IsSuccess);
        return date;
    }

    private static Result<ServiceDate> Rehydrate(
        ServiceDateId dateId,
        IReadOnlyCollection<HelpNeed> needs) =>
        ServiceDate.Rehydrate(
            dateId,
            OrganizationId.New(),
            "Service day",
            "Instructions",
            StartsAt,
            EndsAt,
            StartsAt,
            MembershipId.New(),
            ServiceDateStatus.Draft,
            0,
            needs);
}
