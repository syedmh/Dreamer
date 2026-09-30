using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Services;

namespace HusayniaTabruk.Api.Endpoints.V1.Admin;

internal static class AdminEndpointInputs
{
    public const string IfMatchField = "ifMatch";
    public const string StepUpField = "stepUpToken";
    public const string TargetMembershipIdField = "targetMembershipId";

    public static Result<long> ParseIfMatch(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? value = request.Headers[ApiDefaults.IfMatchHeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Failure<long>(
                MemberAdministrationErrorCodes.Validation(
                    "A valid If-Match header is required."));
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<long>(
                MemberAdministrationErrorCodes.Validation(
                    "A valid If-Match header is required."));
        }

        if (trimmed.Length >= 2
            && trimmed.StartsWith('"')
            && trimmed.EndsWith('"'))
        {
            trimmed = trimmed[1..^1];
        }

        return long.TryParse(trimmed, out long parsed) && parsed >= 0
            ? Result.Success(parsed)
            : Result.Failure<long>(
                MemberAdministrationErrorCodes.Validation(
                    "A valid If-Match header is required."));
    }

    public static Result<StepUpToken> ParseStepUpToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? value = request.Headers[AuthHeaders.StepUpToken].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value)
            ? Result.Failure<StepUpToken>(
                AuthenticationErrorCodes.Unauthorized(
                    AuthenticationErrorCodes.StepUpInvalid,
                    "The step-up token is invalid or has expired."))
            : Result.Success(new StepUpToken(value.Trim()));
    }

    public static Result<MembershipId> ParseMembershipId(string? value)
    {
        return MembershipId.TryParse(value, out MembershipId membershipId)
            ? Result.Success(membershipId)
            : Result.Failure<MembershipId>(
                MemberAdministrationErrorCodes.Validation(
                    "A valid target membership ID is required."));
    }

    public static string BuildInvitationUrl(TabrukAuthOptions authOptions, string invitationToken)
    {
        ArgumentNullException.ThrowIfNull(authOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationToken);

        Uri baseUri = authOptions.GetInvitationBaseUri();
        UriBuilder builder = new(baseUri)
        {
            Fragment = string.Empty,
            Path = $"{baseUri.AbsolutePath.TrimEnd('/')}{ApiDefaults.BasePath}/auth/invitations/accept",
            Query = $"token={Uri.EscapeDataString(invitationToken)}",
        };
        return builder.Uri.AbsoluteUri;
    }
}

internal static class AdminEndpointHeaders
{
    public static void WriteEtag(HttpResponse response, long version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = $"\"{version}\"";
    }
}

internal static class AdminEndpointResults
{
    public static IResult FromResult<T>(
        Result<T> result,
        Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error);
    }

    public static IResult FromResult(Result result, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess() : Problem(result.Error);
    }

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        (int status, string title) = error.Type switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, "Bad request"),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, "Authentication failed"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "Forbidden"),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
            ErrorType.PreconditionFailed => (StatusCodes.Status412PreconditionFailed, "Precondition failed"),
            ErrorType.PayloadTooLarge => (StatusCodes.Status413PayloadTooLarge, "Payload too large"),
            ErrorType.RateLimited => (StatusCodes.Status429TooManyRequests, "Rate limit exceeded"),
            ErrorType.DependencyUnavailable => (StatusCodes.Status503ServiceUnavailable, "Service unavailable"),
            _ => (StatusCodes.Status400BadRequest, "Request failed"),
        };

        return new ProblemHttpResult(status, error.Code, title, error.Message, fieldErrors);
    }

    private sealed class ProblemHttpResult(
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors)
        : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            ApiProblemWriter.WriteAsync(
                httpContext,
                status,
                code,
                title,
                detail,
                fieldErrors);
    }
}

internal sealed record AdminMemberResponse(
    string Id,
    string DisplayName,
    MembershipStatus Status,
    bool EligibleAsNamedParticipant,
    IReadOnlyCollection<OrganizationRole> Roles,
    IReadOnlyCollection<AdminRoleChangeRequestResponse> PendingAdministratorRoleRequests);

internal sealed record AdminRoleChangeRequestResponse(
    string Id,
    string TargetMembershipId,
    AdministratorRoleChangeAction Action,
    string ProposerMembershipId,
    string? ApproverMembershipId,
    string Reason,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ApprovedAt,
    RoleChangeRequestStatus Status);

internal sealed record IssueInvitationResponse(
    string InviteUrl,
    DateTimeOffset ExpiresAt);

internal static class AdminEndpointMappings
{
    public static AdminMemberResponse ToResponse(this AdminMemberSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new AdminMemberResponse(
            summary.Id.ToString(),
            summary.DisplayName,
            summary.Status,
            summary.EligibleAsNamedParticipant,
            summary.Roles,
            summary.PendingAdministratorRoleRequests.Select(request => request.ToResponse()).ToArray());
    }

    public static AdminRoleChangeRequestResponse ToResponse(this AdminRoleChangeRequestSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new AdminRoleChangeRequestResponse(
            summary.Id.ToString(),
            summary.TargetMembershipId.ToString(),
            summary.Action,
            summary.ProposerMembershipId.ToString(),
            summary.ApproverMembershipId?.ToString(),
            summary.Reason,
            summary.ProposedAt,
            summary.ExpiresAt,
            summary.ApprovedAt,
            summary.Status);
    }
}
