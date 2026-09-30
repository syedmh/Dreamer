using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Cancellation;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Application.Signups.Reassignment;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Needs.Reassignment;

public sealed class SignupReassignmentEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule AccountRateLimit =
        new(
            ApiRateLimitPartitions.Account,
            ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
            TimeSpan.FromMinutes(1));
    private static readonly RateLimitRule OrganizationRateLimit =
        new(
            ApiRateLimitPartitions.Organization,
            ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
            TimeSpan.FromHours(1));

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/needs/{needId}/reassign", ReassignAsync)
            .WithName("ReassignWaitlistedSignup")
            .Accepts<ReassignSignupRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountRateLimit, OrganizationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(
                StatusCodes.Status429TooManyRequests,
                ApiDefaults.RetryAfterHeaderName)
            .Produces<SignupResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> ReassignAsync(
        HttpContext httpContext,
        string needId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!HelpNeedId.TryParse(needId, out HelpNeedId parsedNeedId))
        {
            return Problem("A valid help need ID is required.");
        }

        Result<IdempotencyKey> key =
            SignupCancellationEndpointSupport.ParseIdempotencyKey(httpContext.Request);
        if (key.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(key.Error);
        }

        Result<long> version =
            SignupCancellationEndpointSupport.ParseIfMatch(httpContext.Request);
        if (version.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(version.Error);
        }

        Result<ParsedReassignmentRequest> body = await ParseBodyAsync(
            httpContext.Request,
            cancellationToken);
        if (body.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(body.Error);
        }

        SignupReassignmentService service = new(
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<SignupSummary> result = await service.ReassignAsync(
            new ReassignWaitlistedSignupCommand(
                parsedNeedId,
                body.Value.SignupId,
                body.Value.Reason,
                key.Value),
            version.Value,
            cancellationToken);
        if (result.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(result.Error);
        }

        SignupCancellationEndpointSupport.WriteEtag(
            httpContext.Response,
            result.Value.SignupVersion);
        return Results.Ok(SignupCancellationEndpointSupport.ToResponse(result.Value));
    }

    private static async ValueTask<Result<ParsedReassignmentRequest>> ParseBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                request.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    MaxDepth = 8,
                },
                cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("The reassignment request body must be a JSON object.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("signupId" or "reason")
                    || !seen.Add(property.Name))
                {
                    return Invalid(
                        "The reassignment request contains an unsupported or duplicate field.");
                }
            }

            if (!document.RootElement.TryGetProperty("signupId", out JsonElement signupElement)
                || signupElement.ValueKind != JsonValueKind.String
                || !SignupId.TryParse(signupElement.GetString(), out SignupId signupId))
            {
                return Invalid("A valid selected signup ID is required.");
            }

            string? reason = null;
            if (document.RootElement.TryGetProperty("reason", out JsonElement reasonElement))
            {
                if (reasonElement.ValueKind == JsonValueKind.String)
                {
                    reason = reasonElement.GetString();
                }
                else if (reasonElement.ValueKind != JsonValueKind.Null)
                {
                    return Invalid("The reassignment reason must be a string or null.");
                }
            }

            return Result.Success(new ParsedReassignmentRequest(signupId, reason));
        }
        catch (JsonException)
        {
            return Invalid("The reassignment request body is malformed JSON.");
        }
    }

    private static IResult Problem(string message) =>
        SignupCancellationEndpointSupport.Problem(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));

    private static Result<ParsedReassignmentRequest> Invalid(string message) =>
        Result.Failure<ParsedReassignmentRequest>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));
}

public sealed record ReassignSignupRequest(string SignupId, string? Reason);

public sealed record ParsedReassignmentRequest(
    SignupId SignupId,
    string? Reason);
