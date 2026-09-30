using System.Reflection;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Domain.Tests.Signups;

public sealed class SignupTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<SignupStatus, string, bool, SignupStatus> DirectTransitionMatrix
    {
        get
        {
            TheoryData<SignupStatus, string, bool, SignupStatus> data = [];
            foreach (SignupStatus status in Enum.GetValues<SignupStatus>())
            {
                data.Add(status, "approve", status == SignupStatus.Pending, SignupStatus.Approved);
                data.Add(
                    status,
                    "decline",
                    status is SignupStatus.Pending or SignupStatus.Waitlisted,
                    SignupStatus.Declined);
                data.Add(status, "waitlist", status == SignupStatus.Pending, SignupStatus.Waitlisted);
                data.Add(
                    status,
                    "withdraw",
                    status is SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted,
                    status == SignupStatus.Approved ? SignupStatus.Cancelled : SignupStatus.Withdrawn);
                data.Add(
                    status,
                    "cancel",
                    status is SignupStatus.Pending or SignupStatus.Approved or SignupStatus.Waitlisted,
                    SignupStatus.Cancelled);
            }

            return data;
        }
    }

    public static TheoryData<SignupStatus, SignupStatus, bool> OverrideTransitionMatrix
    {
        get
        {
            TheoryData<SignupStatus, SignupStatus, bool> data = [];
            foreach (SignupStatus current in Enum.GetValues<SignupStatus>())
            {
                foreach (SignupStatus target in Enum.GetValues<SignupStatus>())
                {
                    bool allowed =
                        (current is SignupStatus.Pending or SignupStatus.Waitlisted
                            && target is SignupStatus.Approved or SignupStatus.Declined)
                        || (current == SignupStatus.Pending && target == SignupStatus.Waitlisted)
                        || (current == SignupStatus.Approved && target == SignupStatus.Cancelled);
                    data.Add(current, target, allowed);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DirectTransitionMatrix))]
    public void DirectTransitionTableCoversEveryStatus(
        SignupStatus startingStatus,
        string operation,
        bool allowed,
        SignupStatus expectedStatus)
    {
        TestContext context = CreateContext(capacity: 25);
        Signup signup = Rehydrate(context, startingStatus);
        HelpNeedSignups aggregate = CreateAggregate(context, [signup], version: 7);
        AggregateSnapshot before = AggregateSnapshot.Of(aggregate);
        DateTimeOffset commandTime = operation == "withdraw"
            ? context.Date.CancellationDeadlineAt.AddTicks(-1)
            : Now;

        Result<SignupTransitioned> result = operation switch
        {
            "approve" => aggregate.Approve(signup.Id, commandTime),
            "decline" => aggregate.Decline(signup.Id, commandTime),
            "waitlist" => aggregate.Waitlist(signup.Id, commandTime),
            "withdraw" => aggregate.Withdraw(signup.Id, commandTime),
            "cancel" => aggregate.Cancel(signup.Id, commandTime),
            _ => throw new InvalidOperationException(),
        };

        Signup owned = Owned(aggregate, signup.Id);
        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(allowed ? expectedStatus : startingStatus, owned.Status);
        Assert.Equal(allowed ? before.Version + 1 : before.Version, aggregate.Version);
        Assert.Equal(allowed ? signup.Version + 1 : signup.Version, owned.Version);
        if (!allowed)
        {
            Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
            before.AssertUnchanged(aggregate);
        }
    }

    [Theory]
    [MemberData(nameof(OverrideTransitionMatrix))]
    public void OverrideTransitionTableCoversEveryStatusPair(
        SignupStatus startingStatus,
        SignupStatus targetStatus,
        bool allowed)
    {
        TestContext context = CreateContext(capacity: 25);
        Signup signup = Rehydrate(context, startingStatus);
        HelpNeedSignups aggregate = CreateAggregate(context, [signup], version: 9);
        AggregateSnapshot before = AggregateSnapshot.Of(aggregate);

        Result<SignupTransitioned> result =
            aggregate.Override(signup.Id, targetStatus, Now);

        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(
            allowed ? targetStatus : startingStatus,
            Owned(aggregate, signup.Id).Status);
        Assert.Equal(allowed ? before.Version + 1 : before.Version, aggregate.Version);
        if (allowed)
        {
            Assert.True(result.Value.IsOverride);
        }
        else
        {
            Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
            before.AssertUnchanged(aggregate);
        }
    }

    [Theory]
    [InlineData(SignupStatus.Pending, false)]
    [InlineData(SignupStatus.Approved, false)]
    [InlineData(SignupStatus.Waitlisted, true)]
    [InlineData(SignupStatus.Declined, false)]
    [InlineData(SignupStatus.Withdrawn, false)]
    [InlineData(SignupStatus.Cancelled, false)]
    public void ReassignmentTransitionTableCoversEveryStatus(
        SignupStatus startingStatus,
        bool allowed)
    {
        TestContext context = CreateContext(capacity: 25);
        Signup signup = Rehydrate(context, startingStatus);
        HelpNeedSignups aggregate = CreateAggregate(context, [signup], version: 4);
        AggregateSnapshot before = AggregateSnapshot.Of(aggregate);

        Result<WaitlistedSignupReassigned> result =
            aggregate.Reassign(signup.Id, Now);

        Assert.Equal(allowed, result.IsSuccess);
        Assert.Equal(
            allowed ? SignupStatus.Approved : startingStatus,
            Owned(aggregate, signup.Id).Status);
        Assert.Equal(allowed ? before.Version + 1 : before.Version, aggregate.Version);
        if (!allowed)
        {
            Assert.Equal(ErrorCodes.InvalidTransition, result.Error.Code);
            before.AssertUnchanged(aggregate);
        }
    }

    [Theory]
    [InlineData(SignupKind.Individual, 0, 0, 1)]
    [InlineData(SignupKind.Household, 2, 3, 6)]
    [InlineData(SignupKind.Team, 20, 4, 25)]
    public void SubmitCalculatesOnePlusReferencedPlusUnnamed(
        SignupKind kind,
        int referencedCount,
        int unnamedCount,
        int expectedTotal)
    {
        TestContext context = CreateContext(capacity: null);
        HelpNeedSignups aggregate = CreateAggregate(context, [], version: 3);
        SignupId signupId = SignupId.New();

        Result<SignupSubmitted> result = aggregate.Submit(
            signupId,
            context.Primary,
            kind,
            CreateEligibleParticipants(context.OrganizationId, referencedCount),
            unnamedCount,
            Now);

        Assert.True(result.IsSuccess);
        Signup signup = Owned(aggregate, signupId);
        Assert.Equal(expectedTotal, signup.TotalParticipantCount);
        Assert.Equal(referencedCount, signup.MemberParticipantIds.Count);
        Assert.Equal(unnamedCount, signup.UnnamedParticipantCount);
        Assert.Equal(SignupStatus.Pending, signup.Status);
        Assert.Equal(0, signup.Version);
        Assert.Null(signup.LastTransitionAt);
        Assert.Equal(context.Date.Id, signup.ServiceDateId);
        Assert.Equal(4, aggregate.Version);
        Assert.Equal(3, aggregate.OriginalVersion);
    }

    [Fact]
    public void SubmitEnforcesPerTypeAndTotalParticipantBoundaries()
    {
        TestContext context = CreateContext(capacity: null);

        Assert.True(
            Submit(
                context,
                SignupKind.Team,
                CreateEligibleParticipants(context.OrganizationId, 20),
                4).IsSuccess);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(
                context,
                SignupKind.Team,
                CreateEligibleParticipants(context.OrganizationId, 21),
                0).Error.Code);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(context, SignupKind.Team, [], 21).Error.Code);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(context, SignupKind.Team, [], -1).Error.Code);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(
                context,
                SignupKind.Team,
                CreateEligibleParticipants(context.OrganizationId, 20),
                5).Error.Code);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(context, SignupKind.Team, [], 0).Error.Code);
        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(context, SignupKind.Individual, [], 1).Error.Code);
    }

    [Fact]
    public void SubmitRejectsDuplicatePrimaryInactiveIneligibleAndCrossOrganizationReferences()
    {
        TestContext context = CreateContext(capacity: null);
        Membership eligible = CreateMembership(context.OrganizationId, active: true, eligible: true);
        Membership inactive = CreateMembership(context.OrganizationId, active: false, eligible: true);
        Membership ineligible = CreateMembership(context.OrganizationId, active: true, eligible: false);
        Membership foreign = CreateMembership(OrganizationId.New(), active: true, eligible: true);

        Assert.Equal(
            SignupErrorCodes.InvalidSignupInput,
            Submit(context, SignupKind.Team, [eligible, eligible], 0).Error.Code);
        Assert.Equal(
            SignupErrorCodes.IneligibleParticipant,
            Submit(context, SignupKind.Team, [context.Primary], 0).Error.Code);
        Assert.Equal(
            SignupErrorCodes.IneligibleParticipant,
            Submit(context, SignupKind.Team, [inactive], 0).Error.Code);
        Assert.Equal(
            SignupErrorCodes.IneligibleParticipant,
            Submit(context, SignupKind.Team, [ineligible], 0).Error.Code);
        Assert.Equal(
            SignupErrorCodes.IneligibleParticipant,
            Submit(context, SignupKind.Team, [foreign], 0).Error.Code);
    }

    [Fact]
    public void SubmitRejectsDuplicateIdAndActivePrimaryButAllowsTerminalPrimary()
    {
        TestContext context = CreateContext(capacity: null);
        Signup active = Rehydrate(
            context,
            SignupStatus.Pending,
            primaryMembershipId: context.Primary.Id);
        Signup terminal = Rehydrate(
            context,
            SignupStatus.Withdrawn,
            primaryMembershipId: context.Primary.Id);
        HelpNeedSignups activeAggregate = CreateAggregate(context, [active], version: 2);
        HelpNeedSignups terminalAggregate = CreateAggregate(context, [terminal], version: 2);

        AggregateSnapshot activeBefore = AggregateSnapshot.Of(activeAggregate);
        Result<SignupSubmitted> duplicateId = activeAggregate.Submit(
            active.Id,
            context.Primary,
            SignupKind.Individual,
            [],
            0,
            Now);
        Result<SignupSubmitted> duplicatePrimary = activeAggregate.Submit(
            SignupId.New(),
            context.Primary,
            SignupKind.Individual,
            [],
            0,
            Now);
        Result<SignupSubmitted> resubmission = terminalAggregate.Submit(
            SignupId.New(),
            context.Primary,
            SignupKind.Individual,
            [],
            0,
            Now);

        Assert.Equal(ErrorCodes.SignupDuplicate, duplicateId.Error.Code);
        Assert.Equal(ErrorCodes.SignupDuplicate, duplicatePrimary.Error.Code);
        activeBefore.AssertUnchanged(activeAggregate);
        Assert.True(resubmission.IsSuccess);
        Assert.Equal(2, terminalAggregate.Signups.Count);
    }

    [Fact]
    public void SignupContractHasNoParticipantDisplayNameOrFreeTextLabelInput()
    {
        PropertyInfo[] properties = typeof(Signup).GetProperties();
        ParameterInfo[] parameters = typeof(HelpNeedSignups)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .SelectMany(method => method.GetParameters())
            .ToArray();

        Assert.DoesNotContain(
            properties,
            property => property.Name.Contains("DisplayName", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Label", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            parameters,
            parameter => parameter.Name?.Contains("displayName", StringComparison.OrdinalIgnoreCase) == true
                || parameter.Name?.Contains("label", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Theory]
    [InlineData(ServiceDateStatus.Draft, HelpNeedStatus.Open)]
    [InlineData(ServiceDateStatus.Open, HelpNeedStatus.Closed)]
    [InlineData(ServiceDateStatus.Closed, HelpNeedStatus.Closed)]
    [InlineData(ServiceDateStatus.Cancelled, HelpNeedStatus.Closed)]
    public void SubmitAndManagedDecisionsRejectClosedDateOrCategory(
        ServiceDateStatus dateStatus,
        HelpNeedStatus needStatus)
    {
        TestContext context = CreateContext(
            capacity: null,
            dateStatus: dateStatus,
            needStatus: needStatus);
        Signup pending = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = CreateAggregate(context, [pending]);
        AggregateSnapshot before = AggregateSnapshot.Of(aggregate);

        Result<SignupSubmitted> submit = aggregate.Submit(
            SignupId.New(),
            context.Primary,
            SignupKind.Individual,
            [],
            0,
            Now);
        Result<SignupTransitioned> approve = aggregate.Approve(pending.Id, Now);

        Assert.Equal(ErrorCodes.CategoryClosed, submit.Error.Code);
        Assert.Equal(ErrorCodes.CategoryClosed, approve.Error.Code);
        before.AssertUnchanged(aggregate);
    }

    [Fact]
    public void SubmitAndManagedDecisionAllowOneTickBeforeEndAndRejectAtEnd()
    {
        TestContext submitBeforeContext = CreateContext(capacity: null);
        HelpNeedSignups submitBefore = CreateAggregate(submitBeforeContext, []);
        Assert.True(
            submitBefore.Submit(
                SignupId.New(),
                submitBeforeContext.Primary,
                SignupKind.Individual,
                [],
                0,
                submitBeforeContext.Date.EndsAt.AddTicks(-1)).IsSuccess);

        TestContext submitAtContext = CreateContext(capacity: null);
        HelpNeedSignups submitAt = CreateAggregate(submitAtContext, []);
        Assert.Equal(
            ErrorCodes.CategoryClosed,
            submitAt.Submit(
                SignupId.New(),
                submitAtContext.Primary,
                SignupKind.Individual,
                [],
                0,
                submitAtContext.Date.EndsAt).Error.Code);

        TestContext decisionContext = CreateContext(capacity: null);
        Signup pending = Rehydrate(decisionContext, SignupStatus.Pending);
        HelpNeedSignups decision = CreateAggregate(decisionContext, [pending]);
        Assert.Equal(
            ErrorCodes.CategoryClosed,
            decision.Approve(pending.Id, decisionContext.Date.EndsAt).Error.Code);
    }

    [Theory]
    [InlineData(SignupStatus.Pending, SignupStatus.Withdrawn)]
    [InlineData(SignupStatus.Waitlisted, SignupStatus.Withdrawn)]
    [InlineData(SignupStatus.Approved, SignupStatus.Cancelled)]
    public void WithdrawBeforeDeadlineUsesCorrectTerminalState(
        SignupStatus startingStatus,
        SignupStatus expectedStatus)
    {
        TestContext context = CreateContext(capacity: null);
        Signup signup = Rehydrate(context, startingStatus);
        HelpNeedSignups aggregate = CreateAggregate(context, [signup]);

        Result<SignupTransitioned> result = aggregate.Withdraw(
            signup.Id,
            context.Date.CancellationDeadlineAt.AddTicks(-1));

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedStatus, Owned(aggregate, signup.Id).Status);
        Assert.Null(Owned(aggregate, signup.Id).WaitlistOrder);
        Assert.False(result.Value.IsOverride);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void WithdrawAtOrAfterDeadlineFailsWithoutMutation(int ticksAfterDeadline)
    {
        TestContext context = CreateContext(capacity: null);
        Signup signup = Rehydrate(context, SignupStatus.Approved);
        HelpNeedSignups aggregate = CreateAggregate(context, [signup], version: 6);
        AggregateSnapshot before = AggregateSnapshot.Of(aggregate);

        Result<SignupTransitioned> result = aggregate.Withdraw(
            signup.Id,
            context.Date.CancellationDeadlineAt.AddTicks(ticksAfterDeadline));

        Assert.Equal(ErrorCodes.CancellationDeadlinePassed, result.Error.Code);
        before.AssertUnchanged(aggregate);
    }

    [Fact]
    public void ApprovalUsesAllOwnedParticipantSlotsAndHonorsExactCapacityBoundary()
    {
        TestContext context = CreateContext(capacity: 6);
        Signup approved = Rehydrate(
            context,
            SignupStatus.Approved,
            memberParticipantCount: 1,
            unnamedParticipantCount: 1);
        Signup exactFit = Rehydrate(
            context,
            SignupStatus.Pending,
            memberParticipantCount: 1,
            unnamedParticipantCount: 1);
        Signup over = Rehydrate(
            context,
            SignupStatus.Pending,
            unnamedParticipantCount: 1);
        HelpNeedSignups aggregate = CreateAggregate(context, [approved, exactFit, over]);

        Assert.True(aggregate.Approve(exactFit.Id, Now).IsSuccess);
        AggregateSnapshot afterExactFit = AggregateSnapshot.Of(aggregate);
        Result<SignupTransitioned> blocked = aggregate.Approve(over.Id, Now);

        Assert.Equal(ErrorCodes.CapacityUnavailable, blocked.Error.Code);
        afterExactFit.AssertUnchanged(aggregate);
        Assert.Equal(SignupStatus.Pending, Owned(aggregate, over.Id).Status);
    }

    [Fact]
    public void UnlimitedCapacityAcceptsMaximumSizedSignup()
    {
        TestContext context = CreateContext(capacity: null);
        Signup signup = Rehydrate(
            context,
            SignupStatus.Pending,
            memberParticipantCount: 20,
            unnamedParticipantCount: 4);
        HelpNeedSignups aggregate = CreateAggregate(context, [signup]);

        Assert.True(aggregate.Approve(signup.Id, Now).IsSuccess);
        Assert.Equal(SignupStatus.Approved, Owned(aggregate, signup.Id).Status);
    }

    [Fact]
    public void WaitlistOrderUsesHighWaterAndOrderedWaitlistIsDeterministic()
    {
        TestContext context = CreateContext(capacity: 1);
        Signup fourth = Rehydrate(context, SignupStatus.Waitlisted, waitlistOrder: 4);
        Signup second = Rehydrate(context, SignupStatus.Waitlisted, waitlistOrder: 2);
        Signup candidate = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = CreateAggregate(
            context,
            [fourth, candidate, second],
            waitlistOrderHighWater: 7);

        Assert.True(aggregate.Waitlist(candidate.Id, Now).IsSuccess);
        Assert.Equal(8, Owned(aggregate, candidate.Id).WaitlistOrder);
        Assert.Equal(8, aggregate.WaitlistOrderHighWater);
        Assert.Equal(
            [second.Id, fourth.Id, candidate.Id],
            aggregate.OrderedWaitlist().Select(signup => signup.Id).ToArray());
    }

    [Fact]
    public void SelectedWaitlistReassignmentMaySkipEarlierEntryAndRequiresCapacity()
    {
        TestContext context = CreateContext(capacity: 2);
        Signup occupying = Rehydrate(
            context,
            SignupStatus.Approved,
            unnamedParticipantCount: 1);
        Signup first = Rehydrate(context, SignupStatus.Waitlisted, waitlistOrder: 1);
        Signup selected = Rehydrate(
            context,
            SignupStatus.Waitlisted,
            unnamedParticipantCount: 1,
            waitlistOrder: 2);
        HelpNeedSignups aggregate = CreateAggregate(context, [occupying, first, selected], version: 8);
        AggregateSnapshot full = AggregateSnapshot.Of(aggregate);

        Assert.Equal(
            ErrorCodes.CapacityUnavailable,
            aggregate.Reassign(selected.Id, Now).Error.Code);
        full.AssertUnchanged(aggregate);

        Assert.True(aggregate.Cancel(occupying.Id, Now).IsSuccess);
        Assert.True(aggregate.Reassign(selected.Id, Now.AddTicks(1)).IsSuccess);
        Assert.Equal(SignupStatus.Waitlisted, Owned(aggregate, first.Id).Status);
        Assert.Equal(1, Owned(aggregate, first.Id).WaitlistOrder);
        Assert.Equal(SignupStatus.Approved, Owned(aggregate, selected.Id).Status);
        Assert.Null(Owned(aggregate, selected.Id).WaitlistOrder);
        Assert.Equal(2, aggregate.WaitlistOrderHighWater);
    }

    [Fact]
    public void CapacityReleaseAfterWithdrawalAllowsApproval()
    {
        TestContext context = CreateContext(capacity: 2);
        Signup occupying = Rehydrate(
            context,
            SignupStatus.Approved,
            unnamedParticipantCount: 1);
        Signup candidate = Rehydrate(
            context,
            SignupStatus.Pending,
            unnamedParticipantCount: 1);
        HelpNeedSignups aggregate = CreateAggregate(context, [occupying, candidate]);

        Assert.Equal(
            ErrorCodes.CapacityUnavailable,
            aggregate.Approve(candidate.Id, Now).Error.Code);
        Assert.True(
            aggregate.Withdraw(
                occupying.Id,
                context.Date.CancellationDeadlineAt.AddTicks(-1)).IsSuccess);
        Assert.True(
            aggregate.Approve(
                candidate.Id,
                context.Date.CancellationDeadlineAt).IsSuccess);
    }

    [Fact]
    public void RehydrateRejectsInvalidEnumsChronologyCountsAndStateMetadata()
    {
        TestContext context = CreateContext(capacity: null);

        AssertInvalidRehydration(context, kind: (SignupKind)999);
        AssertInvalidRehydration(context, status: (SignupStatus)999);
        AssertInvalidRehydration(context, unnamedParticipantCount: -1);
        AssertInvalidRehydration(context, version: -1);
        AssertInvalidRehydration(
            context,
            status: SignupStatus.Pending,
            lastTransitionAt: Now,
            version: 1);
        AssertInvalidRehydration(
            context,
            status: SignupStatus.Waitlisted,
            lastTransitionAt: Now,
            waitlistOrder: null,
            version: 1);
        AssertInvalidRehydration(
            context,
            status: SignupStatus.Approved,
            submittedAt: Now,
            lastTransitionAt: Now.AddTicks(-1),
            version: 1);
    }

    [Fact]
    public void NonUtcTimestampsThrowBeforeMutation()
    {
        TestContext context = CreateContext(capacity: 25);
        Signup pending = Rehydrate(context, SignupStatus.Pending);
        HelpNeedSignups aggregate = CreateAggregate(context, [pending], version: 11);
        AggregateSnapshot before = AggregateSnapshot.Of(aggregate);
        DateTimeOffset nonUtc = Now.ToOffset(TimeSpan.FromHours(-7));

        Assert.Throws<ArgumentException>(() => aggregate.Approve(pending.Id, nonUtc));
        Assert.Throws<ArgumentException>(() => aggregate.Decline(pending.Id, nonUtc));
        Assert.Throws<ArgumentException>(() => aggregate.Waitlist(pending.Id, nonUtc));
        Assert.Throws<ArgumentException>(() => aggregate.Withdraw(pending.Id, nonUtc));
        Assert.Throws<ArgumentException>(
            () => aggregate.Override(pending.Id, SignupStatus.Approved, nonUtc));
        Assert.Throws<ArgumentException>(() => aggregate.Cancel(pending.Id, nonUtc));
        Assert.Throws<ArgumentException>(
            () => aggregate.Submit(
                SignupId.New(),
                context.Primary,
                SignupKind.Individual,
                [],
                0,
                nonUtc));
        before.AssertUnchanged(aggregate);

        Signup waitlisted = Rehydrate(context, SignupStatus.Waitlisted);
        HelpNeedSignups waitlistAggregate = CreateAggregate(context, [waitlisted]);
        Assert.Throws<ArgumentException>(
            () => waitlistAggregate.Reassign(waitlisted.Id, nonUtc));
    }

    private static Result<SignupSubmitted> Submit(
        TestContext context,
        SignupKind kind,
        IReadOnlyCollection<Membership> participants,
        int unnamedParticipantCount,
        DateTimeOffset? now = null)
    {
        HelpNeedSignups aggregate = CreateAggregate(context, []);
        return aggregate.Submit(
            SignupId.New(),
            context.Primary,
            kind,
            participants,
            unnamedParticipantCount,
            now ?? Now);
    }

    private static Signup Rehydrate(
        TestContext context,
        SignupStatus status,
        SignupId? signupId = null,
        MembershipId? primaryMembershipId = null,
        int memberParticipantCount = 0,
        int unnamedParticipantCount = 0,
        long? waitlistOrder = null,
        long? version = null,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? lastTransitionAt = null)
    {
        if (status == SignupStatus.Waitlisted)
        {
            waitlistOrder ??= 1;
        }

        version ??= status switch
        {
            SignupStatus.Pending => 0,
            _ => 1,
        };
        submittedAt ??= Now.AddHours(-1);
        if (status != SignupStatus.Pending)
        {
            lastTransitionAt ??= Now.AddMinutes(-30);
        }

        return Signup.Rehydrate(
            signupId ?? SignupId.New(),
            context.OrganizationId,
            context.Date.Id,
            context.Need.Id,
            primaryMembershipId ?? MembershipId.New(),
            memberParticipantCount + unnamedParticipantCount == 0
                ? SignupKind.Individual
                : SignupKind.Team,
            Enumerable.Range(0, memberParticipantCount)
                .Select(_ => MembershipId.New())
                .ToArray(),
            unnamedParticipantCount,
            status,
            submittedAt.Value,
            lastTransitionAt,
            waitlistOrder,
            version.Value).Value;
    }

    private static TestContext CreateContext(
        int? capacity,
        ServiceDateStatus dateStatus = ServiceDateStatus.Open,
        HelpNeedStatus needStatus = HelpNeedStatus.Open)
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership primary = CreateMembership(organizationId, active: true, eligible: false);
        ServiceDateId dateId = ServiceDateId.New();
        HelpNeed need = HelpNeed.Rehydrate(
            HelpNeedId.New(),
            dateId,
            HelpCategory.Serving,
            "Serve",
            capacity,
            needStatus,
            needStatus == HelpNeedStatus.Open ? 0 : 1).Value;
        long dateVersion = dateStatus switch
        {
            ServiceDateStatus.Draft => 1,
            ServiceDateStatus.Open => 2,
            ServiceDateStatus.Closed => 3,
            ServiceDateStatus.Cancelled => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(dateStatus)),
        };
        ServiceDate date = ServiceDate.Rehydrate(
            dateId,
            organizationId,
            "Service date",
            "Instructions",
            Now.AddDays(2),
            Now.AddDays(3),
            Now.AddDays(1),
            MembershipId.New(),
            dateStatus,
            dateVersion,
            [need]).Value;
        return new TestContext(organizationId, primary, date, need);
    }

    private static HelpNeedSignups CreateAggregate(
        TestContext context,
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

    private static Membership[] CreateEligibleParticipants(
        OrganizationId organizationId,
        int count) =>
        Enumerable.Range(0, count)
            .Select(_ => CreateMembership(organizationId, active: true, eligible: true))
            .ToArray();

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

    private static Signup Owned(HelpNeedSignups aggregate, SignupId signupId) =>
        aggregate.Signups.Single(signup => signup.Id == signupId);

    private static void AssertInvalidRehydration(
        TestContext context,
        SignupKind kind = SignupKind.Individual,
        SignupStatus status = SignupStatus.Pending,
        int unnamedParticipantCount = 0,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? lastTransitionAt = null,
        long? waitlistOrder = null,
        long version = 0)
    {
        Result<Signup> result = Signup.Rehydrate(
            SignupId.New(),
            context.OrganizationId,
            context.Date.Id,
            context.Need.Id,
            MembershipId.New(),
            kind,
            [],
            unnamedParticipantCount,
            status,
            submittedAt ?? Now,
            lastTransitionAt,
            waitlistOrder,
            version);

        Assert.True(result.IsFailure);
        Assert.Equal(SignupErrorCodes.InvalidSignupInput, result.Error.Code);
    }

    private sealed record TestContext(
        OrganizationId OrganizationId,
        Membership Primary,
        ServiceDate Date,
        HelpNeed Need);

    private sealed record SignupSnapshot(
        SignupId Id,
        SignupStatus Status,
        DateTimeOffset? LastTransitionAt,
        long? WaitlistOrder,
        long Version)
    {
        public static SignupSnapshot Of(Signup signup) =>
            new(
                signup.Id,
                signup.Status,
                signup.LastTransitionAt,
                signup.WaitlistOrder,
                signup.Version);
    }

    private sealed class AggregateSnapshot
    {
        private AggregateSnapshot(
            long originalVersion,
            long version,
            long waitlistOrderHighWater,
            IReadOnlyCollection<SignupSnapshot> signups)
        {
            OriginalVersion = originalVersion;
            Version = version;
            WaitlistOrderHighWater = waitlistOrderHighWater;
            Signups = signups;
        }

        public long OriginalVersion { get; }
        public long Version { get; }
        public long WaitlistOrderHighWater { get; }
        private IReadOnlyCollection<SignupSnapshot> Signups { get; }

        public static AggregateSnapshot Of(HelpNeedSignups aggregate) =>
            new(
                aggregate.OriginalVersion,
                aggregate.Version,
                aggregate.WaitlistOrderHighWater,
                aggregate.Signups
                    .OrderBy(signup => signup.Id.ToString(), StringComparer.Ordinal)
                    .Select(SignupSnapshot.Of)
                    .ToArray());

        public void AssertUnchanged(HelpNeedSignups aggregate)
        {
            Assert.Equal(OriginalVersion, aggregate.OriginalVersion);
            Assert.Equal(Version, aggregate.Version);
            Assert.Equal(WaitlistOrderHighWater, aggregate.WaitlistOrderHighWater);
            Assert.Equal(
                Signups,
                aggregate.Signups
                    .OrderBy(signup => signup.Id.ToString(), StringComparer.Ordinal)
                    .Select(SignupSnapshot.Of)
                    .ToArray());
        }
    }
}
