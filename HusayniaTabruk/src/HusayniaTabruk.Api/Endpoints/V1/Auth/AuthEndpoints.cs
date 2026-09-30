using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Api.Endpoints.V1.Auth;

public sealed class AuthEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule InvitationAcceptanceRateLimit =
        new(ApiRateLimitPartitions.Invitation, 5, TimeSpan.FromMinutes(10));

    private static readonly RateLimitRule InvitationAcceptanceIpRateLimit =
        new(ApiRateLimitPartitions.IpAddress, 50, TimeSpan.FromMinutes(10));

    private static readonly RateLimitRule LoginRateLimit =
        new(ApiRateLimitPartitions.LoginAccount, 10, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule LoginIpRateLimit =
        new(ApiRateLimitPartitions.IpAddress, 100, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule RefreshRateLimit =
        new(ApiRateLimitPartitions.RefreshToken, 30, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule RefreshIpRateLimit =
        new(ApiRateLimitPartitions.IpAddress, 300, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule LogoutRateLimit =
        new(ApiRateLimitPartitions.LogoutActorDevice, 30, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule LogoutIpRateLimit =
        new(ApiRateLimitPartitions.IpAddress, 300, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule StepUpRateLimit =
        new(ApiRateLimitPartitions.Account, 5, TimeSpan.FromMinutes(1));

    public void Map(IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder auth = endpoints.MapGroup("/auth");

        auth.MapPost("/invitations/accept", AcceptInvitationAsync)
            .AllowAnonymous()
            .WithName("AcceptInvitation")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimitKeys(
                ApiRateLimitKeyStrategy.Invitation,
                InvitationAcceptanceRateLimit,
                InvitationAcceptanceIpRateLimit)
            .Produces<TokenSetResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithName("Login")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimitKeys(
                ApiRateLimitKeyStrategy.Login,
                LoginRateLimit,
                LoginIpRateLimit)
            .Produces<TokenSetResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithName("RefreshSession")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimitKeys(
                ApiRateLimitKeyStrategy.Refresh,
                RefreshRateLimit,
                RefreshIpRateLimit)
            .Produces<TokenSetResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        auth.MapPost("/logout", LogoutAsync)
            .AllowAnonymous()
            .WithName("LogoutSession")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimitKeys(
                ApiRateLimitKeyStrategy.Logout,
                LogoutRateLimit,
                LogoutIpRateLimit)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        auth.MapPost("/step-up", StepUpAsync)
            .WithName("IssueStepUp")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(StepUpRateLimit)
            .Produces<StepUpResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> AcceptInvitationAsync(
        HttpContext httpContext,
        AcceptInvitationRequest request,
        AcceptInvitationService service,
        CancellationToken cancellationToken)
    {
        Result<HusayniaTabruk.Domain.Common.Identifiers.DeviceId> installationId =
            AuthEndpointInputs.ParseInstallationId(httpContext.Request);
        if (installationId.IsFailure)
        {
            return AuthEndpointResults.Problem(
                installationId.Error,
                new Dictionary<string, string[]>
                {
                    [AuthEndpointInputs.InstallationIdField] = [AuthEndpointInputs.InstallationIdError],
                });
        }

        Result<HusayniaTabruk.Application.Abstractions.Authentication.TokenPair> result =
            await service.ExecuteAsync(
                new AcceptInvitationCommand(
                    request.Token ?? string.Empty,
                    request.DisplayName ?? string.Empty,
                    request.Password ?? string.Empty,
                    installationId.Value),
                cancellationToken);

        return AuthEndpointResults.FromResult(result, tokens => Results.Ok(TokenSetResponse.From(tokens)));
    }

    private static async Task<IResult> LoginAsync(
        HttpContext httpContext,
        LoginRequest request,
        LoginService service,
        CancellationToken cancellationToken)
    {
        Result<HusayniaTabruk.Domain.Common.Identifiers.DeviceId> installationId =
            AuthEndpointInputs.ParseInstallationId(httpContext.Request);
        if (installationId.IsFailure)
        {
            return AuthEndpointResults.Problem(
                installationId.Error,
                new Dictionary<string, string[]>
                {
                    [AuthEndpointInputs.InstallationIdField] = [AuthEndpointInputs.InstallationIdError],
                });
        }

        Result<HusayniaTabruk.Application.Abstractions.Authentication.TokenPair> result =
            await service.ExecuteAsync(
                new LoginCommand(
                    request.Email ?? string.Empty,
                    request.Password ?? string.Empty,
                    installationId.Value),
                cancellationToken);

        return AuthEndpointResults.FromResult(result, tokens => Results.Ok(TokenSetResponse.From(tokens)));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        RefreshRequest request,
        RefreshSessionService service,
        CancellationToken cancellationToken)
    {
        Result<HusayniaTabruk.Domain.Common.Identifiers.DeviceId> installationId =
            AuthEndpointInputs.ParseInstallationId(httpContext.Request);
        if (installationId.IsFailure)
        {
            return AuthEndpointResults.Problem(
                installationId.Error,
                new Dictionary<string, string[]>
                {
                    [AuthEndpointInputs.InstallationIdField] = [AuthEndpointInputs.InstallationIdError],
                });
        }

        Result<HusayniaTabruk.Application.Abstractions.Authentication.TokenPair> result =
            await service.ExecuteAsync(
                new RefreshSessionCommand(
                    request.RefreshToken ?? string.Empty,
                    installationId.Value),
                cancellationToken);

        return AuthEndpointResults.FromResult(result, tokens => Results.Ok(TokenSetResponse.From(tokens)));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        LogoutRequest request,
        LogoutService service,
        CancellationToken cancellationToken)
    {
        Result<HusayniaTabruk.Domain.Common.Identifiers.DeviceId> installationId =
            AuthEndpointInputs.ParseInstallationId(httpContext.Request);
        if (installationId.IsFailure)
        {
            return AuthEndpointResults.Problem(
                installationId.Error,
                new Dictionary<string, string[]>
                {
                    [AuthEndpointInputs.InstallationIdField] = [AuthEndpointInputs.InstallationIdError],
                });
        }

        Result result = await service.ExecuteAsync(
            new LogoutCommand(
                request.RefreshToken ?? string.Empty,
                installationId.Value),
            cancellationToken);

        return AuthEndpointResults.FromResult(result, Results.NoContent);
    }

    private static async Task<IResult> StepUpAsync(
        StepUpRequest request,
        IssueStepUpService service,
        CancellationToken cancellationToken)
    {
        Result<HusayniaTabruk.Application.Abstractions.Security.StepUpGrant> result =
            await service.ExecuteAsync(
                new StepUpCommand(
                    request.Password ?? string.Empty,
                    request.Purpose ?? string.Empty),
                cancellationToken);

        return AuthEndpointResults.FromResult(
            result,
            grant => Results.Ok(new StepUpResponse(grant.Token.Value, grant.ExpiresAt)));
    }

    private sealed record AcceptInvitationRequest(
        string? Token,
        string? DisplayName,
        string? Password);

    private sealed record LoginRequest(
        string? Email,
        string? Password);

    private sealed record RefreshRequest(string? RefreshToken);

    private sealed record LogoutRequest(string? RefreshToken);

    private sealed record StepUpRequest(
        string? Password,
        string? Purpose);
}
