using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;

namespace HusayniaTabruk.Domain.Tests.Dates;

public sealed class T5IndependentLifecycleTests
{
    [Fact]
    public void CreateTrimsTextAndRetainsIsolationAndManagerFields()
    {
        OrganizationId organizationId = OrganizationId.New();
        MembershipId managerId = MembershipId.New();

        ServiceDate date = Create(
            organizationId: organizationId,
            managerId: managerId,
            title: "  Muharram service  ",
            instructions: "  Arrive early.  ").Value;

        Assert.Equal(organizationId, date.OrganizationId);
        Assert.Equal(managerId, date.ManagerMembershipId);
        Assert.Equal("Muharram service", date.Title);
        Assert.Equal("Arrive early.", date.Instructions);
    }

    [Fact]
    public void CreateRejectsNullTextAndDefaultIdentifiers()
    {
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Create(title: null!).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Create(instructions: null!).Error.Code);

        Assert.Throws<InvalidOperationException>(
            () => ServiceDate.Create(
                default,
                OrganizationId.New(),
                "Date",
                "Instructions",
                StartsAt,
                EndsAt,
                StartsAt,
                MembershipId.New()));
        Assert.Throws<InvalidOperationException>(
            () => ServiceDate.Create(
                ServiceDateId.New(),
                default,
                "Date",
                "Instructions",
                StartsAt,
                EndsAt,
                StartsAt,
                MembershipId.New()));
        Assert.Throws<InvalidOperationException>(
            () => ServiceDate.Create(
                ServiceDateId.New(),
                OrganizationId.New(),
                "Date",
                "Instructions",
                StartsAt,
                EndsAt,
                StartsAt,
                default));
    }

    [Fact]
    public void CreateEnforcesUtcForEveryTimestampBeforeConstructingState()
    {
        DateTimeOffset nonUtc = StartsAt.ToOffset(TimeSpan.FromHours(-7));

        Assert.Throws<ArgumentException>(() => Create(startsAt: nonUtc));
        Assert.Throws<ArgumentException>(() => Create(endsAt: nonUtc));
        Assert.Throws<ArgumentException>(() => Create(deadline: nonUtc));
    }

    [Fact]
    public void CreateAcceptsOneTickDurationAndDeadlineAtStartOrEarlier()
    {
        Assert.True(Create(endsAt: StartsAt.AddTicks(1), deadline: StartsAt).IsSuccess);
        Assert.True(Create(deadline: StartsAt.AddTicks(-1)).IsSuccess);
    }

    [Fact]
    public void CreateRejectsDeadlineOneTickAfterStart()
    {
        Result<ServiceDate> result = Create(deadline: StartsAt.AddTicks(1));

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
    }

    [Fact]
    public void HelpNeedRehydrateAcceptsOneAndMaxCapacityAndRejectsZero()
    {
        Assert.True(CreateNeed(capacity: 1).IsSuccess);
        Assert.True(CreateNeed(capacity: int.MaxValue).IsSuccess);
        Assert.Null(CreateNeed(capacity: null).Value.Capacity);
        Assert.Equal(DateErrorCodes.InvalidDateInput, CreateNeed(capacity: 0).Error.Code);
    }

    [Fact]
    public void HelpNeedRehydrateRejectsDefaultIdentifiersAndUndefinedEnums()
    {
        Assert.Throws<InvalidOperationException>(
            () => HelpNeed.Rehydrate(
                default,
                ServiceDateId.New(),
                HelpCategory.Serving,
                "Serve",
                null,
                HelpNeedStatus.Open,
                0));
        Assert.Throws<InvalidOperationException>(
            () => HelpNeed.Rehydrate(
                HelpNeedId.New(),
                default,
                HelpCategory.Serving,
                "Serve",
                null,
                HelpNeedStatus.Open,
                0));
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            CreateNeed(category: (HelpCategory)(-1)).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            CreateNeed(status: (HelpNeedStatus)int.MaxValue).Error.Code);
    }

    [Fact]
    public void AddNeedAcceptsAllDefinedCategoriesButRejectsUndefinedCategoryWithoutMutation()
    {
        ServiceDate date = Create().Value;

        foreach (HelpCategory category in Enum.GetValues<HelpCategory>())
        {
            Assert.True(
                date.AddNeed(
                    HelpNeedId.New(),
                    category,
                    category.ToString(),
                    null,
                    BeforeStart).IsSuccess);
        }

        long version = date.Version;
        Result<HelpNeedAdded> invalid = date.AddNeed(
            HelpNeedId.New(),
            (HelpCategory)999,
            "Invalid",
            null,
            BeforeStart);

        Assert.True(invalid.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, invalid.Error.Code);
        Assert.Equal(version, date.Version);
        Assert.Equal(Enum.GetValues<HelpCategory>().Length, date.HelpNeeds.Count);
    }

    [Fact]
    public void AddNeedAllowsOneTickBeforeEndAndRejectsAtAndAfterEnd()
    {
        ServiceDate before = Create().Value;
        ServiceDate at = Create().Value;
        ServiceDate after = Create().Value;

        Assert.True(
            before.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Serving,
                "Serve",
                null,
                EndsAt.AddTicks(-1)).IsSuccess);
        Assert.Equal(
            ErrorCodes.InvalidTransition,
            at.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Serving,
                "Serve",
                null,
                EndsAt).Error.Code);
        Assert.Equal(
            ErrorCodes.InvalidTransition,
            after.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Serving,
                "Serve",
                null,
                EndsAt.AddTicks(1)).Error.Code);
    }

    [Fact]
    public void AddNeedRejectsNonUtcAndDefaultIdWithoutMutation()
    {
        ServiceDate date = Create().Value;

        Assert.Throws<ArgumentException>(
            () => date.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Cleanup,
                "Cleanup",
                null,
                BeforeStart.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<InvalidOperationException>(
            () => date.AddNeed(
                default,
                HelpCategory.Cleanup,
                "Cleanup",
                null,
                BeforeStart));
        Assert.Empty(date.HelpNeeds);
        Assert.Equal(0, date.Version);
    }

    [Fact]
    public void DuplicateCategoryIsRejectedEvenWhenInstructionsAndCapacityDiffer()
    {
        ServiceDate date = Create().Value;
        Assert.True(
            date.AddNeed(
                HelpNeedId.New(),
                HelpCategory.FoodPreparation,
                "First",
                1,
                BeforeStart).IsSuccess);

        Result<HelpNeedAdded> duplicate = date.AddNeed(
            HelpNeedId.New(),
            HelpCategory.FoodPreparation,
            "Different",
            int.MaxValue,
            BeforeStart);

        Assert.Equal(DateErrorCodes.DuplicateHelpCategory, duplicate.Error.Code);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal("First", retained.Instructions);
        Assert.Equal(1, retained.Capacity);
        Assert.Equal(1, date.Version);
    }

    [Theory]
    [InlineData(ServiceDateStatus.Draft, true)]
    [InlineData(ServiceDateStatus.Open, false)]
    [InlineData(ServiceDateStatus.Closed, false)]
    [InlineData(ServiceDateStatus.Cancelled, false)]
    [InlineData(ServiceDateStatus.Completed, false)]
    public void OpenTransitionMatrixIsExact(ServiceDateStatus status, bool allowed)
    {
        Result<ServiceDate> rehydrated = Rehydrate(status: status);
        if (status == ServiceDateStatus.Completed)
        {
            Assert.True(rehydrated.IsFailure);
            Assert.Equal(DateErrorCodes.InvalidDateInput, rehydrated.Error.Code);
            return;
        }

        ServiceDate date = rehydrated.Value;
        long originalVersion = date.Version;

        Result<ServiceDateOpened> result = date.Open(StartsAt.AddTicks(-1));

        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(allowed ? ServiceDateStatus.Open : status, date.Status);
        Assert.Equal(originalVersion + (allowed ? 1 : 0), date.Version);
    }

    [Theory]
    [InlineData(ServiceDateStatus.Draft, false)]
    [InlineData(ServiceDateStatus.Open, true)]
    [InlineData(ServiceDateStatus.Closed, false)]
    [InlineData(ServiceDateStatus.Cancelled, false)]
    [InlineData(ServiceDateStatus.Completed, false)]
    public void CloseTransitionMatrixIsExact(ServiceDateStatus status, bool allowed)
    {
        Result<ServiceDate> rehydrated = Rehydrate(status: status);
        if (status == ServiceDateStatus.Completed)
        {
            Assert.True(rehydrated.IsFailure);
            Assert.Equal(DateErrorCodes.InvalidDateInput, rehydrated.Error.Code);
            return;
        }

        ServiceDate date = rehydrated.Value;
        long originalVersion = date.Version;

        Result<ServiceDateClosed> result = date.Close(EndsAt.AddDays(1));

        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(allowed ? ServiceDateStatus.Closed : status, date.Status);
        Assert.Equal(originalVersion + (allowed ? 1 : 0), date.Version);
    }

    [Theory]
    [InlineData(ServiceDateStatus.Draft, true)]
    [InlineData(ServiceDateStatus.Open, true)]
    [InlineData(ServiceDateStatus.Closed, true)]
    [InlineData(ServiceDateStatus.Cancelled, false)]
    [InlineData(ServiceDateStatus.Completed, false)]
    public void CancelTransitionMatrixIsExact(ServiceDateStatus status, bool allowed)
    {
        Result<ServiceDate> rehydrated = Rehydrate(status: status);
        if (status == ServiceDateStatus.Completed)
        {
            Assert.True(rehydrated.IsFailure);
            Assert.Equal(DateErrorCodes.InvalidDateInput, rehydrated.Error.Code);
            return;
        }

        ServiceDate date = rehydrated.Value;
        long originalVersion = date.Version;

        Result<ServiceDateCancelled> result = date.Cancel(EndsAt.AddYears(1));

        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(allowed ? ServiceDateStatus.Cancelled : status, date.Status);
        Assert.Equal(originalVersion + (allowed ? 1 : 0), date.Version);
    }

    [Fact]
    public void LifecycleMethodsRejectNonUtcBeforeAnyRootOrChildMutation()
    {
        HelpNeed need = CreateNeed(serviceDateId: DateId).Value;
        ServiceDate open = Rehydrate(
            status: ServiceDateStatus.Open,
            needs: [need]).Value;
        DateTimeOffset nonUtc = StartsAt.ToOffset(TimeSpan.FromHours(4));

        Assert.Throws<ArgumentException>(() => open.Close(nonUtc));
        Assert.Throws<ArgumentException>(() => open.Cancel(nonUtc));
        Assert.Equal(ServiceDateStatus.Open, open.Status);
        Assert.Equal(2, open.Version);
        Assert.Equal(HelpNeedStatus.Open, Assert.Single(open.HelpNeeds).Status);
        Assert.Equal(7, Assert.Single(open.HelpNeeds).Version);
    }

    [Fact]
    public void CloseAndCancelRetainEveryNeedAndCloseOnlyOpenChildren()
    {
        HelpNeed openNeed = CreateNeed(
            serviceDateId: DateId,
            category: HelpCategory.FoodPreparation,
            version: 2).Value;
        HelpNeed closedNeed = CreateNeed(
            serviceDateId: DateId,
            category: HelpCategory.Serving,
            status: HelpNeedStatus.Closed,
            version: 4).Value;
        ServiceDate date = Rehydrate(
            status: ServiceDateStatus.Open,
            needs: [openNeed, closedNeed]).Value;

        Result<ServiceDateClosed> closed = date.Close(EndsAt);

        Assert.True(closed.IsSuccess);
        Assert.Equal(2, date.HelpNeeds.Count);
        Assert.Equal(2, closed.Value.RetainedHelpNeedIds.Count);
        Assert.Equal(3, date.HelpNeeds.Single(x => x.Category == HelpCategory.FoodPreparation).Version);
        Assert.Equal(4, date.HelpNeeds.Single(x => x.Category == HelpCategory.Serving).Version);

        Result<ServiceDateCancelled> cancelled = date.Cancel(EndsAt.AddTicks(1));

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(2, cancelled.Value.RetainedHelpNeedIds.Count);
        Assert.All(date.HelpNeeds, need => Assert.Equal(HelpNeedStatus.Closed, need.Status));
        Assert.Equal(5, date.Version);
    }

    [Fact]
    public void RehydrateRejectsEveryStructuralCorruption()
    {
        HelpNeed first = CreateNeed(
            id: NeedId,
            serviceDateId: DateId,
            category: HelpCategory.FoodPreparation).Value;
        HelpNeed duplicateId = CreateNeed(
            id: NeedId,
            serviceDateId: DateId,
            category: HelpCategory.Serving).Value;
        HelpNeed duplicateCategory = CreateNeed(
            serviceDateId: DateId,
            category: HelpCategory.FoodPreparation).Value;
        HelpNeed foreign = CreateNeed(
            serviceDateId: ServiceDateId.New(),
            category: HelpCategory.Cleanup).Value;

        AssertInvalidRehydration([first, duplicateId]);
        AssertInvalidRehydration([first, duplicateCategory]);
        AssertInvalidRehydration([first, foreign]);
        AssertInvalidRehydration([first], status: ServiceDateStatus.Closed);
        AssertInvalidRehydration([first], status: ServiceDateStatus.Cancelled);
        AssertInvalidRehydration([first], status: ServiceDateStatus.Completed);
        AssertInvalidRehydration([null!]);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(status: (ServiceDateStatus)999).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(version: -1).Error.Code);
    }

    [Fact]
    public void RehydrateDeepCopiesInputCollectionAndChildren()
    {
        HelpNeed source = CreateNeed(serviceDateId: DateId).Value;
        List<HelpNeed> sourceList = [source];
        ServiceDate date = Rehydrate(needs: sourceList).Value;

        Assert.True(
            date.ChangeNeed(
                source.Id,
                "Mutated source",
                99,
                HelpNeedStatus.Closed,
                BeforeStart).IsSuccess);
        sourceList.Clear();

        Assert.Equal("Need instructions", source.Instructions);
        Assert.Null(source.Capacity);
        Assert.Equal(HelpNeedStatus.Open, source.Status);
        Assert.Equal(7, source.Version);

        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal("Mutated source", retained.Instructions);
        Assert.Equal(99, retained.Capacity);
        Assert.Equal(HelpNeedStatus.Closed, retained.Status);
        Assert.Equal(8, retained.Version);
    }

    [Fact]
    public void HelpNeedSnapshotsAndEventsCannotMutateAggregateOwnedHistory()
    {
        Assert.Null(
            typeof(HelpNeed).GetMethod(
                "Change",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public));

        ServiceDate date = Create().Value;
        Result<HelpNeedAdded> added = date.AddNeed(
            NeedId,
            HelpCategory.Serving,
            "Serve",
            3,
            BeforeStart);
        HelpNeed propertySnapshot = Assert.Single(date.HelpNeeds);

        Assert.True(
            date.ChangeNeed(
                NeedId,
                "Owned mutation",
                8,
                HelpNeedStatus.Closed,
                BeforeStart).IsSuccess);

        HelpNeed owned = Assert.Single(date.HelpNeeds);
        Assert.Equal("Owned mutation", owned.Instructions);
        Assert.Equal(8, owned.Capacity);
        Assert.Equal(HelpNeedStatus.Closed, owned.Status);
        Assert.Equal(1, owned.Version);
        Assert.Equal(1, date.Version);

        Assert.Equal("Serve", propertySnapshot.Instructions);
        Assert.Equal(3, propertySnapshot.Capacity);
        Assert.Equal(HelpNeedStatus.Open, propertySnapshot.Status);
        Assert.Equal(0, propertySnapshot.Version);
        Assert.Equal("Serve", added.Value.HelpNeed.Instructions);
        Assert.Equal(3, added.Value.HelpNeed.Capacity);
        Assert.Equal(HelpNeedStatus.Open, added.Value.HelpNeed.Status);
        Assert.Equal(0, added.Value.HelpNeed.Version);
    }

    [Fact]
    public void RetainedIdEventCollectionsAreImmutableSnapshots()
    {
        ServiceDate date = Create().Value;
        Assert.True(
            date.AddNeed(
                NeedId,
                HelpCategory.Cleanup,
                "Cleanup",
                null,
                BeforeStart).IsSuccess);
        Assert.True(date.Open(BeforeStart).IsSuccess);

        ServiceDateClosed closed = date.Close(EndsAt).Value;
        IList<HelpNeedId> retained = Assert.IsAssignableFrom<IList<HelpNeedId>>(
            closed.RetainedHelpNeedIds);

        Assert.Throws<NotSupportedException>(() => retained.Clear());
        Assert.Equal(NeedId, Assert.Single(date.HelpNeeds).Id);
    }

    [Fact]
    public void HelpNeedChangeThroughParentTrimsAndIncrementsExactlyOnceWhileFailuresPreserveState()
    {
        HelpNeed need = CreateNeed(serviceDateId: DateId).Value;
        ServiceDate date = Rehydrate(needs: [need]).Value;

        Result<HelpNeedChanged> success = date.ChangeNeed(
            need.Id,
            "  Changed  ",
            int.MaxValue,
            HelpNeedStatus.Closed,
            BeforeStart);

        Assert.True(success.IsSuccess);
        Assert.Equal("Changed", Assert.Single(date.HelpNeeds).Instructions);
        Assert.Equal(8, Assert.Single(date.HelpNeeds).Version);
        Assert.Equal(1, date.Version);

        Result<HelpNeedChanged> invalidCapacity = date.ChangeNeed(
            need.Id,
            "Other",
            0,
            HelpNeedStatus.Open,
            BeforeStart);
        Result<HelpNeedChanged> invalidStatus = date.ChangeNeed(
            need.Id,
            "Other",
            1,
            (HelpNeedStatus)999,
            BeforeStart);
        Result<HelpNeedChanged> noOp = date.ChangeNeed(
            need.Id,
            "Changed",
            int.MaxValue,
            HelpNeedStatus.Closed,
            BeforeStart);

        Assert.True(invalidCapacity.IsFailure);
        Assert.True(invalidStatus.IsFailure);
        Assert.Equal(ErrorCodes.InvalidTransition, noOp.Error.Code);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal("Changed", retained.Instructions);
        Assert.Equal(int.MaxValue, retained.Capacity);
        Assert.Equal(HelpNeedStatus.Closed, retained.Status);
        Assert.Equal(8, retained.Version);
        Assert.Equal(1, date.Version);
    }

    [Fact]
    public void AddNeedRejectsDuplicateIdentifierAtomically()
    {
        ServiceDate date = Create().Value;
        Assert.True(
            date.AddNeed(
                NeedId,
                HelpCategory.Serving,
                "Serve",
                3,
                BeforeStart).IsSuccess);

        Result<HelpNeedAdded> duplicate = date.AddNeed(
            NeedId,
            HelpCategory.Cleanup,
            "Cleanup",
            5,
            BeforeStart);

        Assert.True(duplicate.IsFailure);
        Assert.Equal(DateErrorCodes.DuplicateHelpNeed, duplicate.Error.Code);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal(NeedId, retained.Id);
        Assert.Equal(HelpCategory.Serving, retained.Category);
        Assert.Equal(1, date.Version);
    }

    [Fact]
    public void TerminalParentsRejectNeedChangesAndPreserveOwnedState()
    {
        ServiceDate closed = Create().Value;
        Assert.True(
            closed.AddNeed(
                NeedId,
                HelpCategory.Serving,
                "Serve",
                3,
                BeforeStart).IsSuccess);
        Assert.True(closed.Open(BeforeStart).IsSuccess);
        Assert.True(closed.Close(EndsAt).IsSuccess);

        ServiceDate cancelled = Create().Value;
        Assert.True(
            cancelled.AddNeed(
                NeedId,
                HelpCategory.Serving,
                "Serve",
                3,
                BeforeStart).IsSuccess);
        Assert.True(cancelled.Cancel(BeforeStart).IsSuccess);

        foreach (ServiceDate terminal in new[] { closed, cancelled })
        {
            long rootVersion = terminal.Version;
            HelpNeed original = Assert.Single(terminal.HelpNeeds);

            Result<HelpNeedChanged> result = terminal.ChangeNeed(
                NeedId,
                "Reopened",
                9,
                HelpNeedStatus.Open,
                BeforeStart);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
            HelpNeed retained = Assert.Single(terminal.HelpNeeds);
            Assert.Equal(original.Instructions, retained.Instructions);
            Assert.Equal(original.Capacity, retained.Capacity);
            Assert.Equal(original.Status, retained.Status);
            Assert.Equal(original.Version, retained.Version);
            Assert.Equal(rootVersion, terminal.Version);
        }
    }

    [Fact]
    public void ChangeNeedRejectsDefaultAndUnownedIdentifiersWithoutMutation()
    {
        ServiceDate date = Create().Value;
        Assert.True(
            date.AddNeed(
                NeedId,
                HelpCategory.Serving,
                "Serve",
                3,
                BeforeStart).IsSuccess);

        Assert.Throws<InvalidOperationException>(
            () => date.ChangeNeed(
                default,
                "Changed",
                4,
                HelpNeedStatus.Closed,
                BeforeStart));

        Result<HelpNeedChanged> unowned = date.ChangeNeed(
            HelpNeedId.New(),
            "Changed",
            4,
            HelpNeedStatus.Closed,
            BeforeStart);

        Assert.True(unowned.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, unowned.Error.Code);
        HelpNeed retained = Assert.Single(date.HelpNeeds);
        Assert.Equal("Serve", retained.Instructions);
        Assert.Equal(3, retained.Capacity);
        Assert.Equal(HelpNeedStatus.Open, retained.Status);
        Assert.Equal(0, retained.Version);
        Assert.Equal(1, date.Version);
    }

    [Fact]
    public void ExhaustedRootVersionRejectsEveryMutationPathAtomically()
    {
        ServiceDate add = ExhaustRootVersion(Rehydrate().Value);
        ServiceDate open = ExhaustRootVersion(Rehydrate().Value);
        ServiceDate close = ExhaustRootVersion(
            Rehydrate(status: ServiceDateStatus.Open).Value);
        ServiceDate cancel = ExhaustRootVersion(Rehydrate().Value);

        AssertVersionExhausted(
            add.AddNeed(
                HelpNeedId.New(),
                HelpCategory.Cleanup,
                "Cleanup",
                null,
                BeforeStart));
        AssertVersionExhausted(open.Open(BeforeStart));
        AssertVersionExhausted(close.Close(EndsAt));
        AssertVersionExhausted(cancel.Cancel(BeforeStart));

        Assert.Empty(add.HelpNeeds);
        Assert.Equal(ServiceDateStatus.Draft, open.Status);
        Assert.Equal(ServiceDateStatus.Open, close.Status);
        Assert.Equal(ServiceDateStatus.Draft, cancel.Status);
        Assert.All(
            new[] { add, open, close, cancel },
            date => Assert.Equal(long.MaxValue, date.Version));
    }

    [Fact]
    public void ExhaustedChildVersionRejectsEveryChildMutationPathAtomically()
    {
        HelpNeed changeNeed = CreateNeed(
            serviceDateId: DateId,
            version: long.MaxValue).Value;
        ServiceDate change = Rehydrate(needs: [changeNeed]).Value;

        HelpNeed closeNeed = CreateNeed(
            serviceDateId: DateId,
            version: long.MaxValue).Value;
        ServiceDate close = Rehydrate(
            status: ServiceDateStatus.Open,
            needs: [closeNeed]).Value;

        HelpNeed cancelNeed = CreateNeed(
            serviceDateId: DateId,
            version: long.MaxValue).Value;
        ServiceDate cancel = Rehydrate(needs: [cancelNeed]).Value;

        AssertVersionExhausted(
            change.ChangeNeed(
                changeNeed.Id,
                "Changed",
                2,
                HelpNeedStatus.Closed,
                BeforeStart));
        AssertVersionExhausted(close.Close(EndsAt));
        AssertVersionExhausted(cancel.Cancel(BeforeStart));

        Assert.Equal(ServiceDateStatus.Draft, change.Status);
        Assert.Equal(ServiceDateStatus.Open, close.Status);
        Assert.Equal(ServiceDateStatus.Draft, cancel.Status);
        Assert.All(
            new[] { change, close, cancel },
            date =>
            {
                Assert.Equal(
                    date.Status == ServiceDateStatus.Open ? 2 : 1,
                    date.Version);
                HelpNeed retained = Assert.Single(date.HelpNeeds);
                Assert.Equal(HelpNeedStatus.Open, retained.Status);
                Assert.Equal(long.MaxValue, retained.Version);
            });
    }

    [Fact]
    public void RehydrateRejectsUnreachableStatusAndVersionCombinations()
    {
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            CreateNeed(status: HelpNeedStatus.Closed, version: 0).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(status: ServiceDateStatus.Open, version: 0).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(
                status: ServiceDateStatus.Draft,
                version: 0,
                needs: [CreateNeed(serviceDateId: DateId, version: 0).Value]).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(status: ServiceDateStatus.Closed, version: 1).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(status: ServiceDateStatus.Cancelled, version: 0).Error.Code);
        Assert.Equal(
            DateErrorCodes.InvalidDateInput,
            Rehydrate(status: ServiceDateStatus.Completed, version: long.MaxValue).Error.Code);

        Assert.True(Rehydrate(status: ServiceDateStatus.Draft, version: 0).IsSuccess);
        Assert.True(Rehydrate(status: ServiceDateStatus.Open, version: 1).IsSuccess);
        Assert.True(Rehydrate(status: ServiceDateStatus.Closed, version: 2).IsSuccess);
        Assert.True(Rehydrate(status: ServiceDateStatus.Cancelled, version: 1).IsSuccess);
        Assert.True(CreateNeed(status: HelpNeedStatus.Closed, version: 1).IsSuccess);
    }

    [Theory]
    [MemberData(nameof(ReachableServiceDateVersions))]
    public void RehydrateAcceptsMinimalAndEditedReachableVersions(
        int needCount,
        ServiceDateStatus status,
        long version)
    {
        Result<ServiceDate> result = Rehydrate(
            status: status,
            version: version,
            needs: CreateNeeds(needCount, status));

        Assert.True(result.IsSuccess);
        Assert.Equal(version, result.Value.Version);
    }

    [Theory]
    [MemberData(nameof(UnreachableServiceDateVersions))]
    public void RehydrateRejectsVersionsBelowTheReachableMinimum(
        int needCount,
        ServiceDateStatus status,
        long version)
    {
        Result<ServiceDate> result = Rehydrate(
            status: status,
            version: version,
            needs: CreateNeeds(needCount, status));

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
    }

    public static TheoryData<int, ServiceDateStatus, long> ReachableServiceDateVersions()
    {
        TheoryData<int, ServiceDateStatus, long> data = [];

        for (int needCount = 0; needCount <= 2; needCount++)
        {
            data.Add(needCount, ServiceDateStatus.Draft, needCount);
            data.Add(needCount, ServiceDateStatus.Draft, needCount + 1L);
            data.Add(needCount, ServiceDateStatus.Open, needCount + 1L);
            data.Add(needCount, ServiceDateStatus.Open, needCount + 2L);
            data.Add(needCount, ServiceDateStatus.Closed, needCount + 2L);
            data.Add(needCount, ServiceDateStatus.Closed, needCount + 3L);
            data.Add(needCount, ServiceDateStatus.Cancelled, needCount + 1L);
            data.Add(needCount, ServiceDateStatus.Cancelled, needCount + 2L);
            data.Add(needCount, ServiceDateStatus.Cancelled, needCount + 3L);
            data.Add(needCount, ServiceDateStatus.Cancelled, long.MaxValue);
        }

        return data;
    }

    public static TheoryData<int, ServiceDateStatus, long> UnreachableServiceDateVersions()
    {
        TheoryData<int, ServiceDateStatus, long> data = [];

        for (int needCount = 0; needCount <= 2; needCount++)
        {
            AddBelowMinimumVersions(data, needCount, ServiceDateStatus.Draft, needCount);
            AddBelowMinimumVersions(data, needCount, ServiceDateStatus.Open, needCount + 1L);
            AddBelowMinimumVersions(data, needCount, ServiceDateStatus.Closed, needCount + 2L);
            AddBelowMinimumVersions(data, needCount, ServiceDateStatus.Cancelled, needCount + 1L);
        }

        return data;
    }

    private static readonly ServiceDateId DateId = ServiceDateId.New();
    private static readonly HelpNeedId NeedId = HelpNeedId.New();
    private static readonly DateTimeOffset StartsAt =
        new(2026, 8, 20, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndsAt = StartsAt.AddHours(4);
    private static readonly DateTimeOffset BeforeStart = StartsAt.AddDays(-1);

    private static Result<ServiceDate> Create(
        OrganizationId? organizationId = null,
        MembershipId? managerId = null,
        string title = "Service date",
        string instructions = "Instructions",
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        DateTimeOffset? deadline = null) =>
        ServiceDate.Create(
            ServiceDateId.New(),
            organizationId ?? OrganizationId.New(),
            title,
            instructions,
            startsAt ?? StartsAt,
            endsAt ?? EndsAt,
            deadline ?? StartsAt,
            managerId ?? MembershipId.New());

    private static Result<HelpNeed> CreateNeed(
        HelpNeedId? id = null,
        ServiceDateId? serviceDateId = null,
        HelpCategory category = HelpCategory.Cleanup,
        int? capacity = null,
        HelpNeedStatus status = HelpNeedStatus.Open,
        long version = 7) =>
        HelpNeed.Rehydrate(
            id ?? HelpNeedId.New(),
            serviceDateId ?? ServiceDateId.New(),
            category,
            "Need instructions",
            capacity,
            status,
            version);

    private static Result<ServiceDate> Rehydrate(
        ServiceDateStatus status = ServiceDateStatus.Draft,
        long? version = null,
        IReadOnlyCollection<HelpNeed>? needs = null)
    {
        IReadOnlyCollection<HelpNeed> retainedNeeds = needs ?? [];
        long reachableVersion = version ?? status switch
        {
            ServiceDateStatus.Draft => retainedNeeds.Count,
            ServiceDateStatus.Open => retainedNeeds.Count + 1L,
            ServiceDateStatus.Closed => retainedNeeds.Count + 2L,
            ServiceDateStatus.Cancelled => retainedNeeds.Count + 1L,
            _ => 0,
        };

        return ServiceDate.Rehydrate(
            DateId,
            OrganizationId.New(),
            "Service date",
            "Instructions",
            StartsAt,
            EndsAt,
            StartsAt,
            MembershipId.New(),
            status,
            reachableVersion,
            retainedNeeds);
    }

    private static ServiceDate ExhaustRootVersion(ServiceDate date)
    {
        typeof(ServiceDate)
            .GetProperty(nameof(ServiceDate.Version))!
            .SetValue(date, long.MaxValue);
        return date;
    }

    private static HelpNeed[] CreateNeeds(
        int count,
        ServiceDateStatus status)
    {
        HelpCategory[] categories =
        [
            HelpCategory.FoodPreparation,
            HelpCategory.Serving,
        ];
        HelpNeedStatus needStatus = status is ServiceDateStatus.Closed or ServiceDateStatus.Cancelled
            ? HelpNeedStatus.Closed
            : HelpNeedStatus.Open;

        return categories
            .Take(count)
            .Select(category => CreateNeed(
                serviceDateId: DateId,
                category: category,
                status: needStatus,
                version: needStatus == HelpNeedStatus.Closed ? 1 : 0).Value)
            .ToArray();
    }

    private static void AddBelowMinimumVersions(
        TheoryData<int, ServiceDateStatus, long> data,
        int needCount,
        ServiceDateStatus status,
        long minimumVersion)
    {
        data.Add(needCount, status, minimumVersion - 1);
        data.Add(needCount, status, -100);
    }

    private static void AssertInvalidRehydration(
        IReadOnlyCollection<HelpNeed> needs,
        ServiceDateStatus status = ServiceDateStatus.Draft)
    {
        Result<ServiceDate> result = Rehydrate(status: status, needs: needs);

        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.InvalidDateInput, result.Error.Code);
    }

    private static void AssertVersionExhausted<T>(Result<T> result)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(DateErrorCodes.VersionExhausted, result.Error.Code);
    }
}
