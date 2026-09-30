using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Domain.Tests.Signups;

public sealed class T6IndependentSignupTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CapacityCoversZeroOneMaximumAndUnboundedBoundaries()
    {
        Assert.Equal(DateErrorCodes.InvalidDateInput, CreateNeed(ServiceDateId.New(), 0).Error.Code);

        Context one = CreateContext(1);
        Signup onePerson = Rehydrate(one, SignupStatus.Pending);
        HelpNeedSignups oneAggregate = Aggregate(one, [onePerson]);
        Assert.True(oneAggregate.Approve(onePerson.Id, Now).IsSuccess);

        Context maximum = CreateContext(int.MaxValue);
        Signup maximumCandidate = Rehydrate(
            maximum,
            SignupStatus.Pending,
            namedCount: 20,
            unnamedCount: 4);
        HelpNeedSignups maximumAggregate = Aggregate(maximum, [maximumCandidate]);
        Assert.True(maximumAggregate.Approve(maximumCandidate.Id, Now).IsSuccess);

        Context unbounded = CreateContext(null);
        Signup unboundedCandidate = Rehydrate(
            unbounded,
            SignupStatus.Pending,
            namedCount: 20,
            unnamedCount: 4);
        HelpNeedSignups unboundedAggregate = Aggregate(unbounded, [unboundedCandidate]);
        Assert.True(unboundedAggregate.Approve(unboundedCandidate.Id, Now).IsSuccess);
    }

    [Theory]
    [InlineData("External.Signups.Probe", false)]
    [InlineData("HusayniaTabruk.Application", false)]
    [InlineData("HusayniaTabruk.Api", false)]
    [InlineData("HusayniaTabruk.Infrastructure", true)]
    [InlineData("HusayniaTabruk.Domain.Tests", true)]
    public void RehydrationCompilesOnlyForTrustedFriendAssemblies(
        string assemblyName,
        bool expectedToCompile)
    {
        using RehydrationCompileProbe probe = RehydrationCompileProbe.Create(assemblyName);

        CompileResult result = probe.Build();

        if (expectedToCompile)
        {
            Assert.True(result.Succeeded, result.Output);
            return;
        }

        Assert.False(result.Succeeded, result.Output);
        Assert.Contains("SignupProbe.cs", result.Output);
        Assert.Contains("AggregateProbe.cs", result.Output);
    }

    [Fact]
    public void SignupsRehydrationApiSurfaceIsRestrictedToTrustedFriendAssemblies()
    {
        MethodInfo signupRehydrate = typeof(Signup)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == "Rehydrate");
        Assert.True(signupRehydrate.IsAssembly);

        Type factory = typeof(HelpNeedSignups)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name == "PersistenceFactory");
        Assert.True(factory.IsNestedAssembly);

        MethodInfo rehydrate = factory
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "Rehydrate");
        Assert.True(rehydrate.IsAssembly);
        Assert.Equal(
            [
                typeof(ServiceDate),
                typeof(HelpNeed),
                typeof(long),
                typeof(long),
                typeof(IReadOnlyCollection<Signup>),
            ],
            rehydrate.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        Assert.NotNull(typeof(HelpNeedSignups).GetProperty(
            nameof(HelpNeedSignups.WaitlistOrderHighWater)));

        string[] rehydrationEntryPoints = typeof(Signup).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(Signup).Namespace)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Static
                | BindingFlags.DeclaredOnly))
            .Where(method => method.Name == "Rehydrate")
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "HusayniaTabruk.Domain.Signups.HelpNeedSignups+PersistenceFactory.Rehydrate",
                "HusayniaTabruk.Domain.Signups.Signup.Rehydrate",
            ],
            rehydrationEntryPoints);

        Assert.Empty(typeof(Signup).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(HelpNeedSignups).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(
            typeof(Signup).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            IsConstructionOrConversion);
        Assert.DoesNotContain(
            typeof(HelpNeedSignups).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            IsConstructionOrConversion);

        string[] friendAssemblies = typeof(HelpNeedSignups).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["HusayniaTabruk.Domain.Tests", "HusayniaTabruk.Infrastructure"],
            friendAssemblies);
    }

    private static bool IsConstructionOrConversion(MethodInfo method) =>
        Returns(method, method.DeclaringType!)
        || method.Name is "op_Implicit" or "op_Explicit";

    private static bool Returns(MethodInfo method, Type targetType) =>
        method.ReturnType == targetType
        || (method.ReturnType.IsGenericType
            && method.ReturnType.GetGenericArguments().Contains(targetType));

    [Fact]
    public void DuplicateRetryAndMalformedAggregateStateDoNotMutate()
    {
        Context context = CreateContext(25);
        Signup signup = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = Aggregate(context, [signup], version: 4);

        Assert.True(aggregate.Approve(signup.Id, Now).IsSuccess);
        Snapshot approved = Snapshot.Of(aggregate);
        Assert.Equal(
            ErrorCodes.InvalidTransition,
            aggregate.Approve(signup.Id, Now.AddTicks(1)).Error.Code);
        approved.AssertUnchanged(aggregate);

        Signup duplicate = Rehydrate(
            context,
            SignupStatus.Withdrawn,
            id: signup.Id);
        Result<HelpNeedSignups> malformed =
            HelpNeedSignups.PersistenceFactory.Rehydrate(
                context.Date,
                context.Need,
                0,
                0,
                [signup, duplicate]);
        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, malformed.Error.Code);
    }

    [Fact]
    public void DeadlineIsExclusiveAndOverrideRemainsAnExplicitAuthoritySeam()
    {
        Context before = CreateContext(null);
        Signup selfCancelled = Rehydrate(before, SignupStatus.Approved);
        HelpNeedSignups beforeAggregate = Aggregate(before, [selfCancelled]);
        Result<SignupTransitioned> success = beforeAggregate.Withdraw(
            selfCancelled.Id,
            before.Date.CancellationDeadlineAt.AddTicks(-1));
        Assert.True(success.IsSuccess);
        Assert.False(success.Value.IsOverride);
        Assert.Equal(SignupStatus.Cancelled, Owned(beforeAggregate, selfCancelled.Id).Status);

        Context at = CreateContext(null);
        Signup blocked = Rehydrate(at, SignupStatus.Approved);
        HelpNeedSignups atAggregate = Aggregate(at, [blocked]);
        Snapshot original = Snapshot.Of(atAggregate);
        Assert.Equal(
            ErrorCodes.CancellationDeadlinePassed,
            atAggregate.Withdraw(
                blocked.Id,
                at.Date.CancellationDeadlineAt).Error.Code);
        original.AssertUnchanged(atAggregate);

        Result<SignupTransitioned> overridden = atAggregate.Override(
            blocked.Id,
            SignupStatus.Cancelled,
            at.Date.CancellationDeadlineAt);
        Assert.True(overridden.IsSuccess);
        Assert.True(overridden.Value.IsOverride);
        Assert.Equal(SignupStatus.Cancelled, Owned(atAggregate, blocked.Id).Status);
    }

    [Fact]
    public void HouseholdCompositionUsesOnlyMembershipReferencesAndDetachedCounts()
    {
        Context context = CreateContext(null);
        List<Membership> source =
        [
            CreateMembership(context.OrganizationId, active: true, eligible: true),
            CreateMembership(context.OrganizationId, active: true, eligible: true),
        ];
        HelpNeedSignups aggregate = Aggregate(context, []);
        SignupId signupId = SignupId.New();

        Assert.True(
            aggregate.Submit(
                signupId,
                context.Primary,
                SignupKind.Household,
                source,
                3,
                Now).IsSuccess);
        MembershipId[] retained = Owned(aggregate, signupId).MemberParticipantIds.ToArray();

        source.Clear();
        MembershipId[] exposed = Owned(aggregate, signupId).MemberParticipantIds.ToArray();
        exposed[0] = MembershipId.New();

        Signup signup = Owned(aggregate, signupId);
        Assert.Equal(6, signup.TotalParticipantCount);
        Assert.Equal(retained, signup.MemberParticipantIds);
        Assert.Equal(3, signup.UnnamedParticipantCount);
        Assert.DoesNotContain(
            typeof(Signup).GetProperties(),
            property => property.Name.Contains("DisplayName", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Label", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapacityReleaseAllowsSelectedWaitlistReassignmentWithoutOverAllocation()
    {
        Context context = CreateContext(2);
        Signup occupying = Rehydrate(context, SignupStatus.Approved, unnamedCount: 1);
        Signup first = Rehydrate(context, SignupStatus.Waitlisted, waitlistOrder: 1);
        Signup selected = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            waitlistOrder: 2,
            unnamedCount: 1);
        HelpNeedSignups aggregate = Aggregate(context, [occupying, first, selected]);

        Assert.Equal(
            ErrorCodes.CapacityUnavailable,
            aggregate.Reassign(selected.Id, Now).Error.Code);

        Assert.True(aggregate.Cancel(occupying.Id, Now).IsSuccess);
        Assert.True(aggregate.Reassign(selected.Id, Now.AddTicks(1)).IsSuccess);
        Assert.Equal(SignupStatus.Waitlisted, Owned(aggregate, first.Id).Status);
        Assert.Equal(1, Owned(aggregate, first.Id).WaitlistOrder);
        Assert.Equal(SignupStatus.Approved, Owned(aggregate, selected.Id).Status);
        Assert.Null(Owned(aggregate, selected.Id).WaitlistOrder);
    }

    [Fact]
    public void StaleIneligibleAndForeignMembershipsAreRejectedAtomically()
    {
        Context context = CreateContext(null);
        Membership inactive = CreateMembership(context.OrganizationId, active: false, eligible: true);
        Membership ineligible = CreateMembership(context.OrganizationId, active: true, eligible: false);
        Membership foreign = CreateMembership(OrganizationId.New(), active: true, eligible: true);

        foreach (Membership participant in new[] { inactive, ineligible, foreign })
        {
            HelpNeedSignups aggregate = Aggregate(context, [], version: 3);
            Snapshot before = Snapshot.Of(aggregate);
            Result<SignupSubmitted> result = aggregate.Submit(
                SignupId.New(),
                context.Primary,
                SignupKind.Household,
                [participant],
                0,
                Now);
            Assert.True(result.IsFailure);
            Assert.Equal(SignupErrorCodes.IneligibleParticipant, result.Error.Code);
            before.AssertUnchanged(aggregate);
        }
    }

    [Fact]
    public void AggregateRehydrateFreezesVersionAndAcceptsRetainedHighWaterGaps()
    {
        Context context = CreateContext(1);
        Signup approved = Rehydrate(context, SignupStatus.Approved);
        Signup pending = Rehydrate(context, SignupStatus.Pending);

        Result<HelpNeedSignups> negativeVersion =
            HelpNeedSignups.PersistenceFactory.Rehydrate(
                context.Date,
                context.Need,
                signupVersion: -1,
                waitlistOrderHighWater: 0,
                signups: [pending]);
        Result<HelpNeedSignups> negativeHighWater =
            HelpNeedSignups.PersistenceFactory.Rehydrate(
                context.Date,
                context.Need,
                signupVersion: 7,
                waitlistOrderHighWater: -1,
                signups: []);

        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, negativeVersion.Error.Code);
        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, negativeHighWater.Error.Code);

        HelpNeedSignups aggregate = HelpNeedSignups.PersistenceFactory.Rehydrate(
            context.Date,
            context.Need,
            signupVersion: 7,
            waitlistOrderHighWater: 9,
            signups: [approved, pending]).Value;

        Assert.Equal(7, aggregate.OriginalVersion);
        Assert.Equal(9, aggregate.WaitlistOrderHighWater);
        Assert.Equal(ErrorCodes.CapacityUnavailable, aggregate.Approve(pending.Id, Now).Error.Code);

        Assert.True(aggregate.Cancel(approved.Id, Now).IsSuccess);
        Assert.Equal(7, aggregate.OriginalVersion);
        Assert.Equal(9, aggregate.WaitlistOrderHighWater);
    }

    [Fact]
    public void SameOrganizationForeignDateCannotSupplyAReplacementDeadline()
    {
        Context context = CreateContext(null);
        Signup signup = Rehydrate(context, SignupStatus.Approved);
        HelpNeedSignups aggregate = Aggregate(context, [signup]);
        ServiceDateId foreignDateId = ServiceDateId.New();
        HelpNeed forgedNeed = HelpNeed.Rehydrate(
            context.Need.Id,
            foreignDateId,
            HelpCategory.Serving,
            "Forged",
            null,
            HelpNeedStatus.Open,
            0).Value;
        ServiceDate forgedDate = ServiceDate.Rehydrate(
            foreignDateId,
            context.OrganizationId,
            "Foreign date",
            "Foreign instructions",
            Now.AddDays(10),
            Now.AddDays(11),
            Now.AddDays(9),
            MembershipId.New(),
            ServiceDateStatus.Open,
            2,
            [forgedNeed]).Value;
        Snapshot original = Snapshot.Of(aggregate);

        Result<SignupTransitioned> result = aggregate.Withdraw(
            signup.Id,
            context.Date.CancellationDeadlineAt);

        Assert.True(forgedDate.CancellationDeadlineAt > context.Date.CancellationDeadlineAt);
        Assert.Equal(ErrorCodes.CancellationDeadlinePassed, result.Error.Code);
        original.AssertUnchanged(aggregate);
        MethodInfo withdraw = Assert.Single(
            typeof(HelpNeedSignups)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name == nameof(HelpNeedSignups.Withdraw));
        Assert.Equal(
            [typeof(SignupId), typeof(DateTimeOffset)],
            withdraw.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
    }

    [Fact]
    public void RehydrateRejectsUndefinedValuesAndDefaultIds()
    {
        Context context = CreateContext(null);
        Assert.Throws<InvalidOperationException>(
            () => Signup.Rehydrate(
                default,
                context.OrganizationId,
                context.Date.Id,
                context.Need.Id,
                context.Primary.Id,
                SignupKind.Individual,
                [],
                0,
                SignupStatus.Pending,
                Now,
                null,
                null,
                0));
        Assert.Throws<InvalidOperationException>(
            () => Signup.Rehydrate(
                SignupId.New(),
                context.OrganizationId,
                default,
                context.Need.Id,
                context.Primary.Id,
                SignupKind.Individual,
                [],
                0,
                SignupStatus.Pending,
                Now,
                null,
                null,
                0));
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            RehydrateRaw(context, kind: (SignupKind)999).Error.Code);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            RehydrateRaw(context, status: (SignupStatus)999).Error.Code);
    }

    [Theory]
    [InlineData(SignupStatus.Pending, 0L)]
    [InlineData(SignupStatus.Waitlisted, 1L)]
    [InlineData(SignupStatus.Approved, 1L)]
    [InlineData(SignupStatus.Approved, 2L)]
    [InlineData(SignupStatus.Declined, 1L)]
    [InlineData(SignupStatus.Declined, 2L)]
    [InlineData(SignupStatus.Withdrawn, 1L)]
    [InlineData(SignupStatus.Withdrawn, 2L)]
    [InlineData(SignupStatus.Cancelled, 1L)]
    [InlineData(SignupStatus.Cancelled, 2L)]
    [InlineData(SignupStatus.Cancelled, 3L)]
    public void RehydrateAcceptsEveryReachableStatusVersion(
        SignupStatus status,
        long version)
    {
        Context context = CreateContext(null);
        Result<Signup> result = RehydrateRaw(
            context,
            status: status,
            lastTransitionAt: status == SignupStatus.Pending ? null : Now,
            waitlistOrder: status == SignupStatus.Waitlisted ? 1 : null,
            version: version);

        Assert.True(result.IsSuccess);
        Assert.Equal(version, result.Value.Version);
    }

    [Theory]
    [InlineData(SignupStatus.Pending, -1L)]
    [InlineData(SignupStatus.Pending, 1L)]
    [InlineData(SignupStatus.Waitlisted, 0L)]
    [InlineData(SignupStatus.Waitlisted, 2L)]
    [InlineData(SignupStatus.Approved, 0L)]
    [InlineData(SignupStatus.Approved, 3L)]
    [InlineData(SignupStatus.Declined, 0L)]
    [InlineData(SignupStatus.Declined, 3L)]
    [InlineData(SignupStatus.Withdrawn, 0L)]
    [InlineData(SignupStatus.Withdrawn, 3L)]
    [InlineData(SignupStatus.Cancelled, 0L)]
    [InlineData(SignupStatus.Cancelled, 4L)]
    public void RehydrateRejectsEveryUnreachableStatusVersion(
        SignupStatus status,
        long version)
    {
        Context context = CreateContext(null);
        Result<Signup> result = RehydrateRaw(
            context,
            status: status,
            lastTransitionAt: status == SignupStatus.Pending ? null : Now,
            waitlistOrder: status == SignupStatus.Waitlisted ? 1 : null,
            version: version);

        Assert.True(result.IsFailure);
        Assert.Equal(SignupErrorCodes.InvalidSignupInput, result.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsStatusMetadataMismatches()
    {
        Context context = CreateContext(null);
        Result<Signup>[] invalid =
        [
            RehydrateRaw(
                context,
                status: SignupStatus.Pending,
                lastTransitionAt: Now,
                version: 0),
            RehydrateRaw(
                context,
                status: SignupStatus.Waitlisted,
                lastTransitionAt: Now,
                waitlistOrder: null,
                version: 1),
            RehydrateRaw(
                context,
                status: SignupStatus.Approved,
                lastTransitionAt: Now,
                waitlistOrder: 1,
                version: 1),
            RehydrateRaw(
                context,
                status: SignupStatus.Cancelled,
                lastTransitionAt: null,
                version: 1),
        ];

        Assert.All(
            invalid,
            result =>
            {
                Assert.True(result.IsFailure);
                Assert.Equal(SignupErrorCodes.InvalidSignupInput, result.Error.Code);
            });
    }

    [Fact]
    public void AggregateRehydrateRejectsNonCanonicalContextAndInvalidVersion()
    {
        Context context = CreateContext(null);
        Signup signup = Rehydrate(context, SignupStatus.Pending);
        HelpNeed differentDate = HelpNeed.Rehydrate(
            context.Need.Id,
            ServiceDateId.New(),
            context.Need.Category,
            context.Need.Instructions,
            context.Need.Capacity,
            context.Need.Status,
            context.Need.Version).Value;
        HelpNeed differentCapacity = HelpNeed.Rehydrate(
            context.Need.Id,
            context.Date.Id,
            context.Need.Category,
            context.Need.Instructions,
            1,
            context.Need.Status,
            context.Need.Version).Value;
        HelpNeed differentVersion = HelpNeed.Rehydrate(
            context.Need.Id,
            context.Date.Id,
            context.Need.Category,
            context.Need.Instructions,
            context.Need.Capacity,
            context.Need.Status,
            context.Need.Version + 1).Value;
        HelpNeed differentInstructions = HelpNeed.Rehydrate(
            context.Need.Id,
            context.Date.Id,
            context.Need.Category,
            "Different",
            context.Need.Capacity,
            context.Need.Status,
            context.Need.Version).Value;

        Result<HelpNeedSignups>[] invalid =
        [
            HelpNeedSignups.PersistenceFactory.Rehydrate(context.Date, context.Need, -1, 0, [signup]),
            HelpNeedSignups.PersistenceFactory.Rehydrate(context.Date, context.Need, 0, -1, [signup]),
            HelpNeedSignups.PersistenceFactory.Rehydrate(context.Date, differentDate, 0, 0, [signup]),
            HelpNeedSignups.PersistenceFactory.Rehydrate(context.Date, differentCapacity, 0, 0, [signup]),
            HelpNeedSignups.PersistenceFactory.Rehydrate(context.Date, differentVersion, 0, 0, [signup]),
            HelpNeedSignups.PersistenceFactory.Rehydrate(context.Date, differentInstructions, 0, 0, [signup]),
        ];

        Assert.All(
            invalid,
            result =>
            {
                Assert.True(result.IsFailure);
                Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, result.Error.Code);
            });
    }

    [Fact]
    public void AggregateRehydrateRejectsMixedIdentityDuplicateIdsOrdersAndActivePrimary()
    {
        Context context = CreateContext(null);
        Signup valid = Rehydrate(context, SignupStatus.Pending);
        Signup foreignOrganization = RehydrateRaw(
            context,
            organizationId: OrganizationId.New()).Value;
        Signup foreignDate = RehydrateRaw(
            context,
            serviceDateId: ServiceDateId.New()).Value;
        Signup foreignNeed = RehydrateRaw(
            context,
            helpNeedId: HelpNeedId.New()).Value;
        Signup duplicateId = Rehydrate(
            context,
            SignupStatus.Withdrawn,
            id: valid.Id);
        Signup firstWaitlisted = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            waitlistOrder: 7);
        Signup duplicateOrder = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            waitlistOrder: 7);
        MembershipId sharedPrimary = MembershipId.New();
        Signup activeFirst = Rehydrate(
            context,
            SignupStatus.Pending,
            primaryMembershipId: sharedPrimary);
        Signup activeSecond = Rehydrate(
            context,
            SignupStatus.Approved,
            primaryMembershipId: sharedPrimary);

        IReadOnlyCollection<Signup>[] invalidSets =
        [
            [valid, foreignOrganization],
            [valid, foreignDate],
            [valid, foreignNeed],
            [valid, duplicateId],
            [firstWaitlisted, duplicateOrder],
            [activeFirst, activeSecond],
        ];

        Assert.All(
            invalidSets,
            set =>
            {
                Result<HelpNeedSignups> result =
                    HelpNeedSignups.PersistenceFactory.Rehydrate(
                        context.Date,
                        context.Need,
                        0,
                        set
                            .Where(signup => signup.Status == SignupStatus.Waitlisted)
                            .Select(signup => signup.WaitlistOrder!.Value)
                            .DefaultIfEmpty(0)
                            .Max(),
                        set);
                Assert.True(result.IsFailure);
                Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, result.Error.Code);
            });
    }

    [Fact]
    public void AggregateRehydrateRejectsHighWaterBelowCurrentMaximum()
    {
        Context context = CreateContext(null);
        Signup waitlisted = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            waitlistOrder: 4);

        Result<HelpNeedSignups> result =
            HelpNeedSignups.PersistenceFactory.Rehydrate(
                context.Date,
                context.Need,
                signupVersion: 0,
                waitlistOrderHighWater: 3,
                signups: [waitlisted]);

        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, result.Error.Code);
    }

    [Fact]
    public void AggregateRehydrateRejectsAlreadyOverallocatedState()
    {
        Context context = CreateContext(1);
        Signup approved = Rehydrate(
            context,
            SignupStatus.Approved,
            unnamedCount: 1);

        Result<HelpNeedSignups> result =
            HelpNeedSignups.PersistenceFactory.Rehydrate(
                context.Date,
                context.Need,
                0,
                0,
                [approved]);

        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, result.Error.Code);
    }

    [Fact]
    public void RemovedMaximumIsNeverReusedAfterRehydration()
    {
        Context context = CreateContext(null);
        Signup first = Rehydrate(context, SignupStatus.Waitlisted, waitlistOrder: 1);
        Signup removedMaximum = Rehydrate(context, SignupStatus.Waitlisted, waitlistOrder: 2);
        Signup candidate = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = Aggregate(
            context,
            [first, removedMaximum, candidate],
            waitlistOrderHighWater: 2);

        Assert.True(aggregate.Reassign(removedMaximum.Id, Now).IsSuccess);
        Assert.Equal(2, aggregate.WaitlistOrderHighWater);

        HelpNeedSignups rehydrated = HelpNeedSignups.PersistenceFactory.Rehydrate(
            context.Date,
            context.Need,
            aggregate.Version,
            aggregate.WaitlistOrderHighWater,
            aggregate.Signups).Value;

        Assert.True(rehydrated.Waitlist(candidate.Id, Now.AddTicks(1)).IsSuccess);
        Assert.Equal(3, Owned(rehydrated, candidate.Id).WaitlistOrder);
        Assert.Equal(3, rehydrated.WaitlistOrderHighWater);
    }

    [Fact]
    public void UnknownSignupIdFailsWithTypedOwnershipErrorWithoutMutation()
    {
        Context context = CreateContext(null);
        Signup signup = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = Aggregate(context, [signup], version: 5);
        Snapshot before = Snapshot.Of(aggregate);

        Result<SignupTransitioned> result =
            aggregate.Approve(SignupId.New(), Now);

        Assert.Equal(SignupErrorCodes.SignupNotOwned, result.Error.Code);
        before.AssertUnchanged(aggregate);
    }

    [Fact]
    public void ChronologyRejectsPreSubmissionAndPrePriorTransitionAtomically()
    {
        Context context = CreateContext(null);
        Signup pending = Rehydrate(
            context,
            SignupStatus.Pending,
            submittedAt: Now);
        HelpNeedSignups pendingAggregate = Aggregate(context, [pending], version: 2);
        Snapshot pendingBefore = Snapshot.Of(pendingAggregate);

        Result<SignupTransitioned> beforeSubmission =
            pendingAggregate.Approve(pending.Id, Now.AddTicks(-1));

        Assert.Equal(SignupErrorCodes.SignupChronologyInvalid, beforeSubmission.Error.Code);
        pendingBefore.AssertUnchanged(pendingAggregate);

        Signup waitlisted = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            submittedAt: Now.AddHours(-1),
            lastTransitionAt: Now);
        HelpNeedSignups waitlistedAggregate = Aggregate(context, [waitlisted], version: 3);
        Snapshot waitlistedBefore = Snapshot.Of(waitlistedAggregate);

        Result<SignupTransitioned> beforePrior =
            waitlistedAggregate.Decline(waitlisted.Id, Now.AddTicks(-1));

        Assert.Equal(SignupErrorCodes.SignupChronologyInvalid, beforePrior.Error.Code);
        waitlistedBefore.AssertUnchanged(waitlistedAggregate);
    }

    [Fact]
    public void ChronologyAllowsEqualityForSubmissionAndPriorTransition()
    {
        Context context = CreateContext(2);
        Signup pending = Rehydrate(
            context,
            SignupStatus.Pending,
            submittedAt: Now);
        HelpNeedSignups pendingAggregate = Aggregate(context, [pending]);
        Assert.True(pendingAggregate.Approve(pending.Id, Now).IsSuccess);

        Signup waitlisted = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            lastTransitionAt: Now);
        HelpNeedSignups waitlistedAggregate = Aggregate(context, [waitlisted]);
        Assert.True(waitlistedAggregate.Reassign(waitlisted.Id, Now).IsSuccess);
    }

    [Fact]
    public void RootChildAndWaitlistOverflowFailWithoutMutation()
    {
        Context context = CreateContext(25);
        Signup rootCandidate = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups rootExhausted = Aggregate(
            context,
            [rootCandidate],
            version: long.MaxValue);
        Snapshot rootBefore = Snapshot.Of(rootExhausted);
        Assert.Equal(
            SignupErrorCodes.VersionExhausted,
            rootExhausted.Approve(rootCandidate.Id, Now).Error.Code);
        rootBefore.AssertUnchanged(rootExhausted);

        Signup last = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            waitlistOrder: long.MaxValue);
        Signup waitlistCandidate = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups waitlistAggregate = Aggregate(
            context,
            [last, waitlistCandidate],
            waitlistOrderHighWater: long.MaxValue);
        Assert.True(waitlistAggregate.Reassign(last.Id, Now).IsSuccess);

        HelpNeedSignups permanentlyExhausted =
            HelpNeedSignups.PersistenceFactory.Rehydrate(
                context.Date,
                context.Need,
                waitlistAggregate.Version,
                waitlistAggregate.WaitlistOrderHighWater,
                waitlistAggregate.Signups).Value;
        Snapshot waitlistBefore = Snapshot.Of(permanentlyExhausted);
        Assert.Equal(
            SignupErrorCodes.VersionExhausted,
            permanentlyExhausted.Waitlist(waitlistCandidate.Id, Now.AddTicks(1)).Error.Code);
        waitlistBefore.AssertUnchanged(permanentlyExhausted);

        Signup childCandidate = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups childAggregate = Aggregate(context, [childCandidate], version: 6);
        SetOwnedChildVersion(childAggregate, childCandidate.Id, long.MaxValue);
        Snapshot childBefore = Snapshot.Of(childAggregate);
        Assert.Equal(
            SignupErrorCodes.VersionExhausted,
            childAggregate.Cancel(childCandidate.Id, Now).Error.Code);
        childBefore.AssertUnchanged(childAggregate);
    }

    [Fact]
    public void DefensiveCopiesDetachInputsSnapshotsAndOrderedWaitlist()
    {
        Context context = CreateContext(null);
        Signup input = Rehydrate(context, SignupStatus.Waitlisted);
        HelpNeedSignups aggregate = Aggregate(context, [input], version: 2);
        Signup firstSnapshot = Owned(aggregate, input.Id);
        Signup secondSnapshot = Owned(aggregate, input.Id);
        Signup orderedSnapshot = Assert.Single(aggregate.OrderedWaitlist());

        Assert.NotSame(input, firstSnapshot);
        Assert.NotSame(firstSnapshot, secondSnapshot);
        Assert.NotSame(firstSnapshot, orderedSnapshot);

        SetSignupStatus(firstSnapshot, SignupStatus.Declined);
        Assert.Equal(SignupStatus.Waitlisted, input.Status);
        Assert.Equal(SignupStatus.Waitlisted, Owned(aggregate, input.Id).Status);

        Assert.True(aggregate.Reassign(input.Id, Now).IsSuccess);
        Assert.Equal(SignupStatus.Waitlisted, input.Status);
        Assert.Equal(SignupStatus.Approved, Owned(aggregate, input.Id).Status);
    }

    [Fact]
    public void SuccessfulCommandsIncrementRootAndChildExactlyOnceAndPreserveOriginalVersion()
    {
        Context context = CreateContext(25);
        HelpNeedSignups aggregate = Aggregate(context, [], version: 12);
        SignupId signupId = SignupId.New();

        Assert.True(
            aggregate.Submit(
                signupId,
                context.Primary,
                SignupKind.Individual,
                [],
                0,
                Now.AddHours(-1)).IsSuccess);
        Assert.Equal(13, aggregate.Version);
        Assert.Equal(0, Owned(aggregate, signupId).Version);

        Assert.True(aggregate.Waitlist(signupId, Now).IsSuccess);
        Assert.Equal(14, aggregate.Version);
        Assert.Equal(1, Owned(aggregate, signupId).Version);
        Assert.Equal(1, aggregate.WaitlistOrderHighWater);

        Assert.True(aggregate.Reassign(signupId, Now).IsSuccess);
        Assert.Equal(15, aggregate.Version);
        Assert.Equal(2, Owned(aggregate, signupId).Version);
        Assert.Equal(1, aggregate.WaitlistOrderHighWater);

        Assert.True(aggregate.Cancel(signupId, Now).IsSuccess);
        Assert.Equal(16, aggregate.Version);
        Assert.Equal(3, Owned(aggregate, signupId).Version);
        Assert.Equal(SignupStatus.Cancelled, Owned(aggregate, signupId).Status);
        Assert.Equal(12, aggregate.OriginalVersion);
        Assert.Equal(1, aggregate.WaitlistOrderHighWater);
    }

    [Fact]
    public void OverrideWaitlistAllocatesAndAdvancesHighWaterExactlyOnce()
    {
        Context context = CreateContext(null);
        Signup pending = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = Aggregate(
            context,
            [pending],
            version: 5,
            waitlistOrderHighWater: 8);

        Assert.True(
            aggregate.Override(
                pending.Id,
                SignupStatus.Waitlisted,
                Now).IsSuccess);

        Assert.Equal(9, aggregate.WaitlistOrderHighWater);
        Assert.Equal(6, aggregate.Version);
        Assert.Equal(1, Owned(aggregate, pending.Id).Version);
        Assert.Equal(9, Owned(aggregate, pending.Id).WaitlistOrder);
    }

    [Fact]
    public void OmissionResistantApprovalAndReassignmentAlwaysUseOwnedCompleteSet()
    {
        Context context = CreateContext(2);
        Signup occupying = Rehydrate(
            context,
            SignupStatus.Approved,
            unnamedCount: 1);
        Signup pending = Rehydrate(context, SignupStatus.Pending);
        Signup waitlisted = Rehydrate(context, SignupStatus.Waitlisted);
        HelpNeedSignups aggregate = Aggregate(context, [occupying, pending, waitlisted]);
        Snapshot before = Snapshot.Of(aggregate);

        Assert.Equal(
            ErrorCodes.CapacityUnavailable,
            aggregate.Approve(pending.Id, Now).Error.Code);
        Assert.Equal(
            ErrorCodes.CapacityUnavailable,
            aggregate.Reassign(waitlisted.Id, Now).Error.Code);
        before.AssertUnchanged(aggregate);

        MethodInfo[] commands = typeof(HelpNeedSignups)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(
            commands,
            method => method.Name != nameof(HelpNeedSignups.Submit)
                && method.GetParameters().Any(
                    parameter => parameter.ParameterType.IsGenericType
                        && parameter.ParameterType
                            .GetGenericArguments()
                            .Contains(typeof(Signup))));
    }

    [Fact]
    public void UnsafePublicChildAndCollectionAuthorityApisAreAbsent()
    {
        MethodInfo[] signupMethods = typeof(Signup)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        MethodInfo[] aggregateCommands = typeof(HelpNeedSignups)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(
            signupMethods,
            method => method.Name is "Submit"
                or "Approve"
                or "Decline"
                or "Waitlist"
                or "Withdraw"
                or "Override"
                or "Cancel"
                or "ApproveFromWaitlist"
                or "DeepCopy");
        Assert.DoesNotContain(
            aggregateCommands,
            method => method.GetParameters().Any(
                parameter => parameter.ParameterType == typeof(ServiceDate)
                    || parameter.ParameterType == typeof(HelpNeed)
                    || parameter.ParameterType == typeof(Signup)));
        Assert.DoesNotContain(
            aggregateCommands.Where(method => method.Name != nameof(HelpNeedSignups.Submit)),
            method => method.GetParameters().Any(
                parameter => parameter.ParameterType.IsGenericType
                    && parameter.ParameterType.GetGenericArguments().Contains(typeof(Signup))));
        Assert.Null(typeof(Signup).GetProperty("DecidedAt"));
        Assert.NotNull(typeof(Signup).GetProperty(nameof(Signup.LastTransitionAt)));
        Assert.Null(typeof(Signup).Assembly.GetType(
            "HusayniaTabruk.Domain.Signups.SignupCapacity"));
        Assert.Null(typeof(Signup).Assembly.GetType(
            "HusayniaTabruk.Domain.Signups.HelpNeedSignupExtensions"));
    }

    private static Context CreateContext(int? capacity)
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership primary = CreateMembership(organizationId, active: true, eligible: false);
        ServiceDateId dateId = ServiceDateId.New();
        HelpNeed need = CreateNeed(dateId, capacity).Value;
        ServiceDate date = ServiceDate.Rehydrate(
            dateId,
            organizationId,
            "Date",
            "Instructions",
            Now.AddDays(2),
            Now.AddDays(3),
            Now.AddDays(1),
            MembershipId.New(),
            ServiceDateStatus.Open,
            2,
            [need]).Value;
        return new Context(organizationId, primary, date, need);
    }

    private static Result<HelpNeed> CreateNeed(ServiceDateId dateId, int? capacity) =>
        HelpNeed.Rehydrate(
            HelpNeedId.New(),
            dateId,
            HelpCategory.Serving,
            "Serve",
            capacity,
            HelpNeedStatus.Open,
            0);

    private static Membership CreateMembership(
        OrganizationId organizationId,
        bool active,
        bool eligible)
    {
        Membership membership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            eligible).Value;
        if (active)
        {
            membership.Activate(Now);
        }

        return membership;
    }

    private static Signup Rehydrate(
        Context context,
        SignupStatus status,
        SignupId? id = null,
        MembershipId? primaryMembershipId = null,
        int namedCount = 0,
        int unnamedCount = 0,
        long? waitlistOrder = null,
        long? version = null,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? lastTransitionAt = null)
    {
        if (status == SignupStatus.Waitlisted)
        {
            waitlistOrder ??= 1;
        }

        version ??= status == SignupStatus.Pending ? 0 : 1;
        submittedAt ??= Now.AddHours(-1);
        if (status != SignupStatus.Pending)
        {
            lastTransitionAt ??= Now.AddMinutes(-30);
        }

        return RehydrateRaw(
            context,
            id: id,
            primaryMembershipId: primaryMembershipId,
            kind: namedCount + unnamedCount == 0 ? SignupKind.Individual : SignupKind.Team,
            memberParticipantIds: Enumerable.Range(0, namedCount)
                .Select(_ => MembershipId.New())
                .ToArray(),
            unnamedParticipantCount: unnamedCount,
            status: status,
            submittedAt: submittedAt,
            lastTransitionAt: lastTransitionAt,
            waitlistOrder: waitlistOrder,
            version: version.Value).Value;
    }

    private static Result<Signup> RehydrateRaw(
        Context context,
        SignupId? id = null,
        OrganizationId? organizationId = null,
        ServiceDateId? serviceDateId = null,
        HelpNeedId? helpNeedId = null,
        MembershipId? primaryMembershipId = null,
        SignupKind kind = SignupKind.Individual,
        IReadOnlyCollection<MembershipId>? memberParticipantIds = null,
        int unnamedParticipantCount = 0,
        SignupStatus status = SignupStatus.Pending,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? lastTransitionAt = null,
        long? waitlistOrder = null,
        long version = 0) =>
        Signup.Rehydrate(
            id ?? SignupId.New(),
            organizationId ?? context.OrganizationId,
            serviceDateId ?? context.Date.Id,
            helpNeedId ?? context.Need.Id,
            primaryMembershipId ?? MembershipId.New(),
            kind,
            memberParticipantIds ?? [],
            unnamedParticipantCount,
            status,
            submittedAt ?? Now.AddHours(-1),
            lastTransitionAt,
            waitlistOrder,
            version);

    private static HelpNeedSignups Aggregate(
        Context context,
        IReadOnlyCollection<Signup> signups,
        long version = 0,
        long? waitlistOrderHighWater = null) =>
        HelpNeedSignups.PersistenceFactory.Rehydrate(
            context.Date,
            context.Need,
            version,
            waitlistOrderHighWater
                ?? signups
                    .Where(signup => signup.Status == SignupStatus.Waitlisted)
                    .Select(signup => signup.WaitlistOrder!.Value)
                    .DefaultIfEmpty(0)
                    .Max(),
            signups).Value;

    private static Signup Owned(HelpNeedSignups aggregate, SignupId id) =>
        aggregate.Signups.Single(signup => signup.Id == id);

    private static void SetOwnedChildVersion(
        HelpNeedSignups aggregate,
        SignupId signupId,
        long version)
    {
        FieldInfo signupsField = typeof(HelpNeedSignups).GetField(
            "signups",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Owned signup storage field was not found.");
        List<Signup> owned = (List<Signup>)signupsField.GetValue(aggregate)!;
        Signup signup = owned.Single(candidate => candidate.Id == signupId);
        FieldInfo versionField = typeof(Signup).GetField(
            "<Version>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Signup version field was not found.");
        versionField.SetValue(signup, version);
    }

    private static void SetSignupStatus(Signup signup, SignupStatus status)
    {
        FieldInfo statusField = typeof(Signup).GetField(
            "<Status>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Signup status field was not found.");
        statusField.SetValue(signup, status);
    }

    private sealed record CompileResult(bool Succeeded, string Output);

    private sealed class RehydrationCompileProbe : IDisposable
    {
        private readonly string rootPath;
        private readonly string projectPath;

        private RehydrationCompileProbe(string rootPath, string projectPath)
        {
            this.rootPath = rootPath;
            this.projectPath = projectPath;
        }

        public static RehydrationCompileProbe Create(string assemblyName)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "HusayniaTabruk.SignupsCompileProbe",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            string domainProjectPath = FindProject(
                "src",
                "HusayniaTabruk.Domain",
                "HusayniaTabruk.Domain.csproj");
            string projectPath = Path.Combine(rootPath, "CompileProbe.csproj");
            File.WriteAllText(
                projectPath,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>{{assemblyName}}</AssemblyName>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{domainProjectPath}}" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(rootPath, "SignupProbe.cs"),
                """
                using System;
                using HusayniaTabruk.Domain.Common.Enums;
                using HusayniaTabruk.Domain.Common.Identifiers;
                using HusayniaTabruk.Domain.Signups;

                public static class SignupProbe
                {
                    public static void Compile() =>
                        _ = Signup.Rehydrate(
                            default,
                            default,
                            default,
                            default,
                            default,
                            SignupKind.Individual,
                            Array.Empty<MembershipId>(),
                            0,
                            SignupStatus.Pending,
                            default,
                            null,
                            null,
                            0);
                }
                """);
            File.WriteAllText(
                Path.Combine(rootPath, "AggregateProbe.cs"),
                """
                using System;
                using HusayniaTabruk.Domain.Signups;

                public static class AggregateProbe
                {
                    public static void Compile() =>
                        _ = HelpNeedSignups.PersistenceFactory.Rehydrate(
                            default!,
                            default!,
                            0,
                            0,
                            Array.Empty<Signup>());
                }
                """);

            return new RehydrationCompileProbe(rootPath, projectPath);
        }

        public CompileResult Build()
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" --nologo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Could not start the compile probe.");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new CompileResult(
                process.ExitCode == 0,
                $"{output}{Environment.NewLine}{error}");
        }

        public void Dispose()
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        private static string FindProject(params string[] relativePath)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                string candidate = Path.Combine([directory.FullName, .. relativePath]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repository root.");
        }
    }

    private sealed record Context(
        OrganizationId OrganizationId,
        Membership Primary,
        ServiceDate Date,
        HelpNeed Need);

    private sealed record ChildSnapshot(
        SignupId Id,
        SignupStatus Status,
        DateTimeOffset? LastTransitionAt,
        long? WaitlistOrder,
        long Version)
    {
        public static ChildSnapshot Of(Signup signup) =>
            new(
                signup.Id,
                signup.Status,
                signup.LastTransitionAt,
                signup.WaitlistOrder,
                signup.Version);
    }

    private sealed class Snapshot
    {
        private Snapshot(
            long originalVersion,
            long version,
            long waitlistOrderHighWater,
            IReadOnlyCollection<ChildSnapshot> children)
        {
            OriginalVersion = originalVersion;
            Version = version;
            WaitlistOrderHighWater = waitlistOrderHighWater;
            Children = children;
        }

        private long OriginalVersion { get; }
        private long Version { get; }
        private long WaitlistOrderHighWater { get; }
        private IReadOnlyCollection<ChildSnapshot> Children { get; }

        public static Snapshot Of(HelpNeedSignups aggregate) =>
            new(
                aggregate.OriginalVersion,
                aggregate.Version,
                aggregate.WaitlistOrderHighWater,
                aggregate.Signups
                    .OrderBy(signup => signup.Id.ToString(), StringComparer.Ordinal)
                    .Select(ChildSnapshot.Of)
                    .ToArray());

        public void AssertUnchanged(HelpNeedSignups aggregate)
        {
            Assert.Equal(OriginalVersion, aggregate.OriginalVersion);
            Assert.Equal(Version, aggregate.Version);
            Assert.Equal(WaitlistOrderHighWater, aggregate.WaitlistOrderHighWater);
            Assert.Equal(
                Children,
                aggregate.Signups
                    .OrderBy(signup => signup.Id.ToString(), StringComparer.Ordinal)
                    .Select(ChildSnapshot.Of)
                    .ToArray());
        }
    }
}
