using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Application.Signups.Participants;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Signups.Participants;

public sealed class EligibleParticipantQueryServiceTests
{
    [Fact]
    public async Task ListRevalidatesActorAndUsesLiveAuthorityForTheProjection()
    {
        CurrentActor claimed = new();
        ActiveMembershipContext liveActor = ActiveActor(
            claimed.UserId,
            claimed.MembershipId,
            claimed.OrganizationId);
        MembershipRepository membershipRepository = new(
            Result.Success(liveActor));
        ParticipantRepository participantRepository = new(
            Result.Success(
                new EligibleSignupParticipantSlice(
                [
                    new EligibleSignupParticipantSummary(
                        MembershipId.New(),
                        "First eligible member"),
                    new EligibleSignupParticipantSummary(
                        MembershipId.New(),
                        "Second eligible member"),
                ],
                HasMore: true)));
        EligibleParticipantQueryService service = new(
            participantRepository,
            membershipRepository,
            claimed);

        Result<EligibleSignupParticipantPage> result = await service.ListAsync(
            Convert.ToBase64String("4"u8.ToArray()),
            pageSize: 2);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Equal(
            Convert.ToBase64String("6"u8.ToArray()),
            result.Value.NextCursor);
        Assert.Equal(1, membershipRepository.ResolveCalls);
        Assert.Equal(claimed.UserId, membershipRepository.ResolvedUserId);
        Assert.Equal(claimed.MembershipId, membershipRepository.ResolvedMembershipId);
        Assert.Equal(claimed.OrganizationId, membershipRepository.ResolvedOrganizationId);
        Assert.Equal(1, participantRepository.Calls);
        Assert.Equal(liveActor.OrganizationId, participantRepository.OrganizationId);
        Assert.Equal(liveActor.MembershipId, participantRepository.ExcludedMembershipId);
        Assert.Equal(4, participantRepository.StartIndex);
        Assert.Equal(2, participantRepository.PageSize);
    }

    [Theory]
    [InlineData("not-base64", null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    public async Task InvalidPaginationIsRejectedAfterAuthorityRevalidation(
        string? cursor,
        int? pageSize)
    {
        MembershipRepository membershipRepository = new(
            Result.Success(
                ActiveActor(
                    UserId.New(),
                    MembershipId.New(),
                    OrganizationId.New())));
        ParticipantRepository participantRepository = new(
            Result.Success(
                new EligibleSignupParticipantSlice([], HasMore: false)));
        EligibleParticipantQueryService service = new(
            participantRepository,
            membershipRepository,
            new CurrentActor());

        Result<EligibleSignupParticipantPage> result = await service.ListAsync(
            cursor,
            pageSize);

        Assert.True(result.IsFailure);
        Assert.Equal(
            EligibleParticipantErrorCodes.InvalidParticipantQuery,
            result.Error.Code);
        Assert.Equal(1, membershipRepository.ResolveCalls);
        Assert.Equal(0, participantRepository.Calls);
    }

    [Fact]
    public async Task PageSizeAboveMaximumIsRejected()
    {
        CurrentActor actor = new();
        MembershipRepository membershipRepository = new(
            Result.Success(
                ActiveActor(
                    actor.UserId,
                    actor.MembershipId,
                    actor.OrganizationId)));
        ParticipantRepository participantRepository = new(
            Result.Success(
                new EligibleSignupParticipantSlice([], HasMore: false)));
        EligibleParticipantQueryService service = new(
            participantRepository,
            membershipRepository,
            actor);

        Result<EligibleSignupParticipantPage> result = await service.ListAsync(
            cursor: null,
            pageSize: ApplicationLimits.MaximumPageSize + 1);

        Assert.True(result.IsFailure);
        Assert.Equal(
            EligibleParticipantErrorCodes.InvalidParticipantQuery,
            result.Error.Code);
        Assert.Equal(1, membershipRepository.ResolveCalls);
        Assert.Equal(0, participantRepository.Calls);
    }

    [Fact]
    public async Task InactiveActorIsDeniedBeforeTheParticipantProjection()
    {
        MembershipRepository membershipRepository = new(
            Result.Failure<ActiveMembershipContext>(
                AuthenticationErrorCodes.Unauthorized(
                    AuthenticationErrorCodes.AuthenticationFailed,
                    "Authentication failed.")));
        ParticipantRepository participantRepository = new(
            Result.Success(
                new EligibleSignupParticipantSlice([], HasMore: false)));
        EligibleParticipantQueryService service = new(
            participantRepository,
            membershipRepository,
            new CurrentActor());

        Result<EligibleSignupParticipantPage> result = await service.ListAsync(
            cursor: "not-base64",
            pageSize: 0);

        Assert.True(result.IsFailure);
        Assert.Equal(
            AuthenticationErrorCodes.AuthenticationFailed,
            result.Error.Code);
        Assert.Equal(1, membershipRepository.ResolveCalls);
        Assert.Equal(0, participantRepository.Calls);
    }

    private static ActiveMembershipContext ActiveActor(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId) =>
        new(
            userId,
            membershipId,
            organizationId,
            "Active member",
            EligibleAsNamedParticipant: true,
            [OrganizationRole.Admin],
            "Organization",
            "America/Los_Angeles");

    private sealed class CurrentActor : ICurrentActor
    {
        public UserId UserId { get; } = UserId.New();
        public MembershipId MembershipId { get; } = MembershipId.New();
        public OrganizationId OrganizationId { get; } = OrganizationId.New();
    }

    private sealed class ParticipantRepository(
        Result<EligibleSignupParticipantSlice> result)
        : IEligibleSignupParticipantRepository
    {
        public int Calls { get; private set; }
        public OrganizationId OrganizationId { get; private set; }
        public MembershipId ExcludedMembershipId { get; private set; }
        public int StartIndex { get; private set; }
        public int PageSize { get; private set; }

        public ValueTask<Result<EligibleSignupParticipantSlice>> ListEligibleAsync(
            OrganizationId organizationId,
            MembershipId excludedMembershipId,
            int startIndex,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            OrganizationId = organizationId;
            ExcludedMembershipId = excludedMembershipId;
            StartIndex = startIndex;
            PageSize = pageSize;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MembershipRepository(
        Result<ActiveMembershipContext> result)
        : IMembershipRepository
    {
        public int ResolveCalls { get; private set; }
        public UserId ResolvedUserId { get; private set; }
        public MembershipId ResolvedMembershipId { get; private set; }
        public OrganizationId ResolvedOrganizationId { get; private set; }

        public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            ResolvedUserId = userId;
            ResolvedMembershipId = membershipId;
            ResolvedOrganizationId = organizationId;
            return ValueTask.FromResult(result);
        }

        public ValueTask<Result<InvitationAcceptanceContext>>
            GetInvitationAcceptanceContextAsync(
                string invitationToken,
                CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public ValueTask<Result> AcceptInvitationAsync(
            string invitationToken,
            MembershipId membershipId,
            string displayName,
            DateTimeOffset acceptedAt,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
            UserId userId,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public ValueTask<Result> SaveGovernanceAsync(
            OrganizationAccountGovernance aggregate,
            MembershipId actorMembershipId,
            DateTimeOffset occurredAt,
            MembershipAdministrationPersistenceEffects effects,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        public ValueTask<Result> IssueInvitationAsync(
            IssueMembershipInvitationPersistenceRequest request,
            CancellationToken cancellationToken = default) =>
            throw Unexpected();

        private static InvalidOperationException Unexpected() =>
            new("Unexpected membership repository call.");
    }
}
