using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Accounts;

public sealed class AuthenticationServicesTests
{
    [Fact]
    public async Task AcceptInvitationSetsPasswordActivatesMembershipAndIssuesTokensInsideUnitOfWork()
    {
        RecordingUnitOfWork unitOfWork = new();
        RecordingMembershipRepository membershipRepository = new()
        {
            InvitationAcceptanceContext = Result.Success(
                new InvitationAcceptanceContext(UserId.New(), MembershipId.New(), OrganizationId.New())),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        RecordingIdentityService identityService = new()
        {
            SetPasswordResult = Result.Success(),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        TokenPair expectedTokens = new(
            "access-token",
            new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero),
            "refresh-token",
            new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero));
        RecordingTokenService tokenService = new()
        {
            IssueResult = expectedTokens,
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        AcceptInvitationService service = new(
            unitOfWork,
            membershipRepository,
            identityService,
            tokenService,
            new StubClock(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero)));

        Result<TokenPair> result = await service.ExecuteAsync(
            new AcceptInvitationCommand(
                " invite-token ",
                "  Accepted Member  ",
                "Passw0rd!Passw0rd!",
                DeviceId.New()));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(expectedTokens, result.Value);
        Assert.Equal(1, unitOfWork.ExecuteCount);
        Assert.Equal(["get-invitation", "set-password", "accept-invitation", "issue-token"], unitOfWork.ExecutionLog);
        Assert.Equal(" invite-token ", membershipRepository.RequestedInvitationToken);
        Assert.Equal("Accepted Member", membershipRepository.AcceptedDisplayName);
    }

    [Fact]
    public async Task AcceptInvitationBlankDisplayNameFailsBeforeInfrastructureCalls()
    {
        RecordingUnitOfWork unitOfWork = new();
        AcceptInvitationService service = new(
            unitOfWork,
            new RecordingMembershipRepository(),
            new RecordingIdentityService(),
            new RecordingTokenService(),
            new StubClock(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero)));

        Result<TokenPair> result = await service.ExecuteAsync(
            new AcceptInvitationCommand(
                "invite-token",
                "   ",
                "Passw0rd!Passw0rd!",
                DeviceId.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.InvalidAuthInput, result.Error.Code);
        Assert.Equal(0, unitOfWork.ExecuteCount);
    }

    [Fact]
    public async Task AcceptInvitationDoesNotIssueTokensWhenAtomicAcceptanceLosesTheRace()
    {
        RecordingUnitOfWork unitOfWork = new();
        RecordingMembershipRepository membershipRepository = new()
        {
            InvitationAcceptanceContext = Result.Success(
                new InvitationAcceptanceContext(UserId.New(), MembershipId.New(), OrganizationId.New())),
            AcceptInvitationResult = Result.Failure(
                AuthenticationErrorCodes.Unauthorized(
                    AuthenticationErrorCodes.InvitationInvalid,
                    "The invitation is invalid or has expired.")),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        RecordingIdentityService identityService = new()
        {
            SetPasswordResult = Result.Success(),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        RecordingTokenService tokenService = new()
        {
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        AcceptInvitationService service = new(
            unitOfWork,
            membershipRepository,
            identityService,
            tokenService,
            new StubClock(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero)));

        Result<TokenPair> result = await service.ExecuteAsync(
            new AcceptInvitationCommand(
                "invite-token",
                "Accepted Member",
                "Passw0rd!Passw0rd!",
                DeviceId.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.InvitationInvalid, result.Error.Code);
        Assert.Equal(["get-invitation", "set-password", "accept-invitation"], unitOfWork.ExecutionLog);
        Assert.Equal(0, tokenService.IssueCount);
    }

    [Fact]
    public async Task LoginVerifiesCredentialsBeforeOpeningTheUnitOfWorkAndIssuingTokens()
    {
        RecordingUnitOfWork unitOfWork = new();
        UserId userId = UserId.New();
        MembershipId membershipId = MembershipId.New();
        OrganizationId organizationId = OrganizationId.New();
        RecordingIdentityService identityService = new()
        {
            VerifyCredentialsResult = Result.Success(userId),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        RecordingMembershipRepository membershipRepository = new()
        {
            ActiveMembershipContext = Result.Success(
                new ActiveMembershipContext(
                    userId,
                    membershipId,
                    organizationId,
                    "Member",
                    true,
                    [OrganizationRole.FoodIncharge],
                    "Org",
                    "America/Los_Angeles")),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        RecordingTokenService tokenService = new()
        {
            IssueResult = new TokenPair(
                "access-token",
                new DateTimeOffset(2026, 8, 16, 17, 10, 0, TimeSpan.Zero),
                "refresh-token",
                new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero)),
            ExecutionLog = unitOfWork.ExecutionLog,
        };
        LoginService service = new(unitOfWork, identityService, membershipRepository, tokenService);

        Result<TokenPair> result = await service.ExecuteAsync(
            new LoginCommand(
                "member@example.test",
                "Passw0rd!Passw0rd!",
                DeviceId.New()));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(
            ["verify-credentials", "get-active-membership", "issue-token"],
            unitOfWork.ExecutionLog);
        Assert.Equal(1, unitOfWork.ExecuteCount);
    }

    [Fact]
    public async Task LoginDoesNotIssueTokensWhenTheMembershipIsNotActive()
    {
        RecordingUnitOfWork unitOfWork = new();
        RecordingIdentityService identityService = new()
        {
            VerifyCredentialsResult = Result.Success(UserId.New()),
        };
        RecordingMembershipRepository membershipRepository = new()
        {
            ActiveMembershipContext = Result.Failure<ActiveMembershipContext>(
                AuthenticationErrorCodes.Unauthorized(
                    AuthenticationErrorCodes.AuthenticationFailed,
                    "Authentication failed.")),
        };
        RecordingTokenService tokenService = new();
        LoginService service = new(unitOfWork, identityService, membershipRepository, tokenService);

        Result<TokenPair> result = await service.ExecuteAsync(
            new LoginCommand(
                "member@example.test",
                "Passw0rd!Passw0rd!",
                DeviceId.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.AuthenticationFailed, result.Error.Code);
        Assert.Equal(0, unitOfWork.ExecuteCount);
        Assert.Equal(0, tokenService.IssueCount);
    }

    [Fact]
    public async Task RefreshSessionDelegatesToTheTokenService()
    {
        TokenPair expected = new(
            "new-access-token",
            new DateTimeOffset(2026, 8, 16, 17, 10, 0, TimeSpan.Zero),
            "new-refresh-token",
            new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero));
        RecordingTokenService tokenService = new()
        {
            RotateResult = Result.Success(expected),
        };
        RefreshSessionService service = new(tokenService);

        Result<TokenPair> result = await service.ExecuteAsync(
            new RefreshSessionCommand("refresh-token", DeviceId.New()));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(expected, result.Value);
        Assert.Equal(1, tokenService.RotateCount);
    }

    [Fact]
    public async Task IssueStepUpPassesTheCurrentUserAndPurposeToTheVerifier()
    {
        UserId userId = UserId.New();
        RecordingStepUpVerifier verifier = new()
        {
            IssueResult = Result.Success(
                new StepUpGrant(
                    new StepUpToken("step-up-token"),
                    new StepUpPurpose("governance.assign"),
                    new DateTimeOffset(2026, 8, 16, 17, 5, 0, TimeSpan.Zero))),
        };
        IssueStepUpService service = new(
            new StubCurrentActor(userId, MembershipId.New(), OrganizationId.New()),
            verifier);

        Result<StepUpGrant> result = await service.ExecuteAsync(
            new StepUpCommand("Passw0rd!Passw0rd!", "governance.assign"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(userId, verifier.IssuedUserId);
        Assert.Equal("governance.assign", verifier.IssuedPurpose!.Value.Value);
    }

    [Fact]
    public async Task IssueStepUpBlankPurposeFailsBeforeCallingTheVerifier()
    {
        RecordingStepUpVerifier verifier = new();
        IssueStepUpService service = new(
            new StubCurrentActor(UserId.New(), MembershipId.New(), OrganizationId.New()),
            verifier);

        Result<StepUpGrant> result = await service.ExecuteAsync(
            new StepUpCommand("Passw0rd!Passw0rd!", " "));

        Assert.True(result.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.InvalidAuthInput, result.Error.Code);
        Assert.Equal(0, verifier.IssueCount);
    }

    [Fact]
    public async Task AcceptInvitationPropagatesCancellationTokenToUnitOfWorkAndDependencies()
    {
        RecordingUnitOfWork unitOfWork = new();
        RecordingMembershipRepository membershipRepository = new()
        {
            InvitationAcceptanceContext = Result.Success(
                new InvitationAcceptanceContext(UserId.New(), MembershipId.New(), OrganizationId.New())),
        };
        RecordingIdentityService identityService = new()
        {
            SetPasswordResult = Result.Success(),
        };
        RecordingTokenService tokenService = new()
        {
            IssueResult = new TokenPair(
                "access-token",
                new DateTimeOffset(2026, 8, 16, 17, 10, 0, TimeSpan.Zero),
                "refresh-token",
                new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero)),
        };
        AcceptInvitationService service = new(
            unitOfWork,
            membershipRepository,
            identityService,
            tokenService,
            new StubClock(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero)));
        using CancellationTokenSource cancellation = new();

        Result<TokenPair> result = await service.ExecuteAsync(
            new AcceptInvitationCommand(
                "invite-token",
                "Accepted Member",
                "Passw0rd!Passw0rd!",
                DeviceId.New()),
            cancellation.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(cancellation.Token, unitOfWork.LastCancellationToken);
        Assert.Equal(cancellation.Token, membershipRepository.GetInvitationCancellationToken);
        Assert.Equal(cancellation.Token, identityService.SetPasswordCancellationToken);
        Assert.Equal(cancellation.Token, membershipRepository.AcceptInvitationCancellationToken);
        Assert.Equal(cancellation.Token, tokenService.IssueCancellationToken);
    }

    [Fact]
    public async Task LoginPropagatesCancellationTokenThroughMembershipLookupAndTokenIssuance()
    {
        RecordingUnitOfWork unitOfWork = new();
        RecordingIdentityService identityService = new()
        {
            VerifyCredentialsResult = Result.Success(UserId.New()),
        };
        RecordingMembershipRepository membershipRepository = new()
        {
            ActiveMembershipContext = Result.Success(
                new ActiveMembershipContext(
                    UserId.New(),
                    MembershipId.New(),
                    OrganizationId.New(),
                    "Member",
                    true,
                    [],
                    "Org",
                    "America/Los_Angeles")),
        };
        RecordingTokenService tokenService = new()
        {
            IssueResult = new TokenPair(
                "access-token",
                new DateTimeOffset(2026, 8, 16, 17, 10, 0, TimeSpan.Zero),
                "refresh-token",
                new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero)),
        };
        LoginService service = new(unitOfWork, identityService, membershipRepository, tokenService);
        using CancellationTokenSource cancellation = new();

        Result<TokenPair> result = await service.ExecuteAsync(
            new LoginCommand(
                "member@example.test",
                "Passw0rd!Passw0rd!",
                DeviceId.New()),
            cancellation.Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(cancellation.Token, identityService.VerifyCredentialsCancellationToken);
        Assert.Equal(cancellation.Token, membershipRepository.GetActiveMembershipCancellationToken);
        Assert.Equal(cancellation.Token, unitOfWork.LastCancellationToken);
        Assert.Equal(cancellation.Token, tokenService.IssueCancellationToken);
    }

    [Fact]
    public async Task RefreshLogoutAndStepUpPropagateCancellationTokenToTheirDependencies()
    {
        RecordingTokenService tokenService = new()
        {
            RotateResult = Result.Success(
                new TokenPair(
                    "access-token",
                    new DateTimeOffset(2026, 8, 16, 17, 10, 0, TimeSpan.Zero),
                    "refresh-token",
                    new DateTimeOffset(2026, 9, 15, 17, 0, 0, TimeSpan.Zero))),
        };
        RecordingStepUpVerifier verifier = new()
        {
            IssueResult = Result.Success(
                new StepUpGrant(
                    new StepUpToken("step-up-token"),
                    new StepUpPurpose("governance.assign"),
                    new DateTimeOffset(2026, 8, 16, 17, 5, 0, TimeSpan.Zero))),
        };
        RefreshSessionService refresh = new(tokenService);
        LogoutService logout = new(tokenService);
        IssueStepUpService stepUp = new(
            new StubCurrentActor(UserId.New(), MembershipId.New(), OrganizationId.New()),
            verifier);
        using CancellationTokenSource cancellation = new();

        Result<TokenPair> refreshResult = await refresh.ExecuteAsync(
            new RefreshSessionCommand("refresh-token", DeviceId.New()),
            cancellation.Token);
        Result logoutResult = await logout.ExecuteAsync(
            new LogoutCommand("refresh-token", DeviceId.New()),
            cancellation.Token);
        Result<StepUpGrant> stepUpResult = await stepUp.ExecuteAsync(
            new StepUpCommand("Passw0rd!Passw0rd!", "governance.assign"),
            cancellation.Token);

        Assert.True(refreshResult.IsSuccess, refreshResult.IsFailure ? refreshResult.Error.Message : null);
        Assert.True(logoutResult.IsSuccess, logoutResult.IsFailure ? logoutResult.Error.Message : null);
        Assert.True(stepUpResult.IsSuccess, stepUpResult.IsFailure ? stepUpResult.Error.Message : null);
        Assert.Equal(cancellation.Token, tokenService.RotateCancellationToken);
        Assert.Equal(cancellation.Token, tokenService.RevokeCancellationToken);
        Assert.Equal(cancellation.Token, verifier.IssueCancellationToken);
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int ExecuteCount { get; private set; }
        public List<string> ExecutionLog { get; } = [];
        public CancellationToken LastCancellationToken { get; private set; }

        public async ValueTask<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<Result<T>>> operation,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            LastCancellationToken = cancellationToken;
            return await operation(cancellationToken);
        }
    }

    private sealed class RecordingMembershipRepository : IMembershipRepository
    {
        public List<string>? ExecutionLog { get; set; }
        public Result<InvitationAcceptanceContext>? InvitationAcceptanceContext { get; set; }
        public Result<ActiveMembershipContext>? ActiveMembershipContext { get; set; }
        public Result? AcceptInvitationResult { get; set; }
        public string? RequestedInvitationToken { get; private set; }
        public string? AcceptedDisplayName { get; private set; }
        public CancellationToken GetInvitationCancellationToken { get; private set; }
        public CancellationToken AcceptInvitationCancellationToken { get; private set; }
        public CancellationToken GetActiveMembershipCancellationToken { get; private set; }
        public CancellationToken ResolveActiveActorCancellationToken { get; private set; }

        public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result.Failure<OrganizationAccountGovernance>(
                    MemberAdministrationErrorCodes.Validation(
                        "The test did not configure governance loading.")));

        public ValueTask<Result> SaveGovernanceAsync(
            OrganizationAccountGovernance aggregate,
            MembershipId actorMembershipId,
            DateTimeOffset occurredAt,
            MembershipAdministrationPersistenceEffects effects,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> IssueInvitationAsync(
            IssueMembershipInvitationPersistenceRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextAsync(
            string invitationToken,
            CancellationToken cancellationToken = default)
        {
            ExecutionLog?.Add("get-invitation");
            RequestedInvitationToken = invitationToken;
            GetInvitationCancellationToken = cancellationToken;
            return ValueTask.FromResult(
                InvitationAcceptanceContext
                ?? Result.Failure<InvitationAcceptanceContext>(
                    AuthenticationErrorCodes.Unauthorized(
                        AuthenticationErrorCodes.InvitationInvalid,
                        "The invitation is invalid or has expired.")));
        }

        public ValueTask<Result> AcceptInvitationAsync(
            string invitationToken,
            MembershipId membershipId,
            string displayName,
            DateTimeOffset acceptedAt,
            CancellationToken cancellationToken = default)
        {
            ExecutionLog?.Add("accept-invitation");
            AcceptedDisplayName = displayName;
            AcceptInvitationCancellationToken = cancellationToken;
            return ValueTask.FromResult(AcceptInvitationResult ?? Result.Success());
        }

        public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
            UserId userId,
            CancellationToken cancellationToken = default)
        {
            GetActiveMembershipCancellationToken = cancellationToken;
            return GetActiveMembershipResult();
        }

        public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken = default)
        {
            ResolveActiveActorCancellationToken = cancellationToken;
            return GetActiveMembershipAsync(userId, cancellationToken);
        }

        private ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipResult()
        {
            ExecutionLog?.Add("get-active-membership");
            return ValueTask.FromResult(
                ActiveMembershipContext
                ?? Result.Failure<ActiveMembershipContext>(
                    AuthenticationErrorCodes.Unauthorized(
                        AuthenticationErrorCodes.AuthenticationFailed,
                        "Authentication failed.")));
        }
    }

    private sealed class RecordingIdentityService : IIdentityService
    {
        public List<string>? ExecutionLog { get; set; }
        public Result<UserId>? VerifyCredentialsResult { get; set; }
        public Result? SetPasswordResult { get; set; }
        public CancellationToken VerifyCredentialsCancellationToken { get; private set; }
        public CancellationToken SetPasswordCancellationToken { get; private set; }
        public CancellationToken VerifyPasswordCancellationToken { get; private set; }

        public ValueTask<Result<UserId>> VerifyCredentialsAsync(
            string login,
            string password,
            CancellationToken cancellationToken = default)
        {
            VerifyCredentialsCancellationToken = cancellationToken;
            return GetVerifyCredentialsResult();
        }

        public ValueTask<Result> SetPasswordAsync(
            UserId userId,
            string password,
            CancellationToken cancellationToken = default)
        {
            SetPasswordCancellationToken = cancellationToken;
            return GetSetPasswordResult();
        }

        public ValueTask<Result> VerifyPasswordAsync(
            UserId userId,
            string password,
            CancellationToken cancellationToken = default)
        {
            VerifyPasswordCancellationToken = cancellationToken;
            return ValueTask.FromResult(Result.Success());
        }

        private ValueTask<Result<UserId>> GetVerifyCredentialsResult()
        {
            ExecutionLog?.Add("verify-credentials");
            return ValueTask.FromResult(
                VerifyCredentialsResult
                ?? Result.Failure<UserId>(
                    AuthenticationErrorCodes.Unauthorized(
                        AuthenticationErrorCodes.AuthenticationFailed,
                        "Authentication failed.")));
        }

        private ValueTask<Result> GetSetPasswordResult()
        {
            ExecutionLog?.Add("set-password");
            return ValueTask.FromResult(SetPasswordResult ?? Result.Success());
        }
    }

    private sealed class RecordingTokenService : ITokenService
    {
        public List<string>? ExecutionLog { get; set; }
        public TokenPair IssueResult { get; set; } = new(
            "access-token",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(30));

        public Result<TokenPair>? RotateResult { get; set; }
        public int IssueCount { get; private set; }
        public int RotateCount { get; private set; }
        public CancellationToken IssueCancellationToken { get; private set; }
        public CancellationToken RotateCancellationToken { get; private set; }
        public CancellationToken RevokeCancellationToken { get; private set; }

        public ValueTask<TokenPair> IssueAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            DeviceId deviceId,
            CancellationToken cancellationToken = default)
        {
            IssueCount++;
            ExecutionLog?.Add("issue-token");
            IssueCancellationToken = cancellationToken;
            return ValueTask.FromResult(IssueResult);
        }

        public ValueTask<Result<TokenPair>> RotateAsync(
            string refreshToken,
            DeviceId deviceId,
            CancellationToken cancellationToken = default)
        {
            RotateCount++;
            RotateCancellationToken = cancellationToken;
            return ValueTask.FromResult(RotateResult ?? Result.Success(IssueResult));
        }

        public ValueTask RevokeAsync(
            string refreshToken,
            DeviceId deviceId,
            CancellationToken cancellationToken = default)
        {
            RevokeCancellationToken = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStepUpVerifier : IStepUpVerifier
    {
        public int IssueCount { get; private set; }
        public UserId IssuedUserId { get; private set; }
        public StepUpPurpose? IssuedPurpose { get; private set; }
        public Result<StepUpGrant>? IssueResult { get; set; }
        public CancellationToken IssueCancellationToken { get; private set; }
        public CancellationToken ConsumeCancellationToken { get; private set; }

        public ValueTask<Result<StepUpGrant>> IssueAsync(
            UserId userId,
            string credential,
            StepUpPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            IssueCount++;
            IssuedUserId = userId;
            IssuedPurpose = purpose;
            IssueCancellationToken = cancellationToken;
            return ValueTask.FromResult(
                IssueResult
                ?? Result.Failure<StepUpGrant>(
                    AuthenticationErrorCodes.Unauthorized(
                        AuthenticationErrorCodes.AuthenticationFailed,
                        "Authentication failed.")));
        }

        public ValueTask<Result> ConsumeAsync(
            UserId userId,
            StepUpToken token,
            StepUpPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            ConsumeCancellationToken = cancellationToken;
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed record StubCurrentActor(
        UserId UserId,
        MembershipId MembershipId,
        OrganizationId OrganizationId) : ICurrentActor;

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
