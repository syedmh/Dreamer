using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Husaynia.Web.Areas.Admin.Prayer;

internal static class PrayerAdminEndpoints
{
    private const int MaximumRequestBytes = 32_768;
    private const string CreateAction = "prayer.profile.create";
    private const string ActivateAction = "prayer.profile.activate";
    private const string SaveOverrideAction = "prayer.override.save";
    private const string DeactivateOverrideAction = "prayer.override.deactivate";
    private const string RefreshAction = "prayer.snapshot.refresh";
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/prayer");
        group.MapPost("/profiles", CreateProfileAsync);
        group.MapPost("/profiles/{profileId:guid}/activate", ActivateProfileAsync);
        group.MapPut("/overrides", SaveOverrideAsync);
        group.MapDelete("/overrides/{overrideId:guid}", DeactivateOverrideAsync);
        group.MapPost("/refresh", RefreshAsync);
    }

    private static async Task<IResult> CreateProfileAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IAntiforgery antiforgery,
        ICorrelationContext correlation,
        IIdentityAuditFinalizer auditFinalizer,
        IPrayerAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlation, CreateAction);
        var admission = await AuthorizeAsync(
                httpContext,
                authorizationService,
                authorizer,
                auditFinalizer,
                CreateAction,
                "PrayerProfile",
                "new")
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(
                httpContext,
                antiforgery,
                auditFinalizer,
                admission.Actor!,
                CreateAction,
                "PrayerProfile",
                "new")
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        CreateProfileRequest request;
        try
        {
            request = await ReadBoundedJsonAsync<CreateProfileRequest>(
                    httpContext.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrayerRequestException exception)
        {
            return await TransportFailureAsync(
                    auditFinalizer,
                    admission.Actor!,
                    CreateAction,
                    "PrayerProfile",
                    "new",
                    exception.Code,
                    exception.Message,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.CreateProfileAsync(
                    new(
                        request.ProviderKind,
                        request.Latitude,
                        request.Longitude,
                        request.MethodJson,
                        request.AlgorithmVersion,
                        request.EffectiveFrom),
                    ToUserContext(admission.Actor!),
                    cancellationToken),
                auditFinalizer,
                admission.Actor!,
                CreateAction,
                "PrayerProfile",
                "new",
                correlation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> ActivateProfileAsync(
        HttpContext httpContext,
        Guid profileId,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IAntiforgery antiforgery,
        ICorrelationContext correlation,
        IIdentityAuditFinalizer auditFinalizer,
        IPrayerAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlation, ActivateAction);
        var targetId = profileId.ToString("N");
        var admission = await AuthorizeAsync(
                httpContext,
                authorizationService,
                authorizer,
                auditFinalizer,
                ActivateAction,
                "PrayerProfile",
                targetId)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(
                httpContext,
                antiforgery,
                auditFinalizer,
                admission.Actor!,
                ActivateAction,
                "PrayerProfile",
                targetId)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        ActivateProfileRequest request;
        try
        {
            request = await ReadBoundedJsonAsync<ActivateProfileRequest>(
                    httpContext.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrayerRequestException exception)
        {
            return await TransportFailureAsync(
                    auditFinalizer,
                    admission.Actor!,
                    ActivateAction,
                    "PrayerProfile",
                    targetId,
                    exception.Code,
                    exception.Message,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.ActivateProfileAsync(
                    new(profileId, new RowVersion(request.ExpectedStateRowVersion)),
                    ToUserContext(admission.Actor!),
                    cancellationToken),
                auditFinalizer,
                admission.Actor!,
                ActivateAction,
                "PrayerProfile",
                targetId,
                correlation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> SaveOverrideAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IAntiforgery antiforgery,
        ICorrelationContext correlation,
        IIdentityAuditFinalizer auditFinalizer,
        IPrayerAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlation, SaveOverrideAction);
        var admission = await AuthorizeAsync(
                httpContext,
                authorizationService,
                authorizer,
                auditFinalizer,
                SaveOverrideAction,
                "PrayerOverride",
                "request")
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(
                httpContext,
                antiforgery,
                auditFinalizer,
                admission.Actor!,
                SaveOverrideAction,
                "PrayerOverride",
                "request")
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        SaveOverrideRequest request;
        try
        {
            request = await ReadBoundedJsonAsync<SaveOverrideRequest>(
                    httpContext.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrayerRequestException exception)
        {
            return await TransportFailureAsync(
                    auditFinalizer,
                    admission.Actor!,
                    SaveOverrideAction,
                    "PrayerOverride",
                    "request",
                    exception.Code,
                    exception.Message,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.SaveOverrideAsync(
                    new(
                        request.ProfileHash,
                        request.Date,
                        request.PrayerKey,
                        request.LocalTime,
                        request.Reason),
                    ToUserContext(admission.Actor!),
                    cancellationToken),
                auditFinalizer,
                admission.Actor!,
                SaveOverrideAction,
                "PrayerOverride",
                "request",
                correlation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> DeactivateOverrideAsync(
        HttpContext httpContext,
        Guid overrideId,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IAntiforgery antiforgery,
        ICorrelationContext correlation,
        IIdentityAuditFinalizer auditFinalizer,
        IPrayerAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlation, DeactivateOverrideAction);
        var targetId = overrideId.ToString("N");
        var admission = await AuthorizeAsync(
                httpContext,
                authorizationService,
                authorizer,
                auditFinalizer,
                DeactivateOverrideAction,
                "PrayerOverride",
                targetId)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(
                httpContext,
                antiforgery,
                auditFinalizer,
                admission.Actor!,
                DeactivateOverrideAction,
                "PrayerOverride",
                targetId)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        DeactivateOverrideRequest request;
        try
        {
            request = await ReadBoundedJsonAsync<DeactivateOverrideRequest>(
                    httpContext.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrayerRequestException exception)
        {
            return await TransportFailureAsync(
                    auditFinalizer,
                    admission.Actor!,
                    DeactivateOverrideAction,
                    "PrayerOverride",
                    targetId,
                    exception.Code,
                    exception.Message,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.DeactivateOverrideAsync(
                    new(overrideId, new RowVersion(request.ExpectedRowVersion)),
                    ToUserContext(admission.Actor!),
                    cancellationToken),
                auditFinalizer,
                admission.Actor!,
                DeactivateOverrideAction,
                "PrayerOverride",
                targetId,
                correlation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IAntiforgery antiforgery,
        ICorrelationContext correlation,
        IIdentityAuditFinalizer auditFinalizer,
        IPrayerAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlation, RefreshAction);
        var admission = await AuthorizeAsync(
                httpContext,
                authorizationService,
                authorizer,
                auditFinalizer,
                RefreshAction,
                "PrayerSnapshot",
                "request")
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(
                httpContext,
                antiforgery,
                auditFinalizer,
                admission.Actor!,
                RefreshAction,
                "PrayerSnapshot",
                "request")
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        RefreshRequest request;
        try
        {
            request = await ReadBoundedJsonAsync<RefreshRequest>(
                    httpContext.Request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrayerRequestException exception)
        {
            return await TransportFailureAsync(
                    auditFinalizer,
                    admission.Actor!,
                    RefreshAction,
                    "PrayerSnapshot",
                    "request",
                    exception.Code,
                    exception.Message,
                    correlation)
                .ConfigureAwait(false);
        }

        try
        {
            var command = new RefreshPrayerScheduleCommand(
                request.ProfileHash,
                new YearMonth(request.Year, request.Month));
            return await InvokeAdministrationAsync(
                    () => administration.RefreshAsync(
                        command,
                        ToUserContext(admission.Actor!),
                        cancellationToken),
                    auditFinalizer,
                    admission.Actor!,
                    RefreshAction,
                    "PrayerSnapshot",
                    "request",
                    correlation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException)
        {
            return await TransportFailureAsync(
                    auditFinalizer,
                    admission.Actor!,
                    RefreshAction,
                    "PrayerSnapshot",
                    "request",
                    "invalid_request",
                    "The refresh month is invalid.",
                    correlation)
                .ConfigureAwait(false);
        }

    }

    private static async Task<PrayerAdmission> AuthorizeAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        string action,
        string targetType,
        string targetId)
    {
        var actor = await AuthenticateActorAsync(httpContext).ConfigureAwait(false);
        var policy = await authorizationService.AuthorizeAsync(
                httpContext.User,
                PolicyNames.SiteAdministration)
            .ConfigureAwait(false);
        var capability = authorizer.Authorize(
            actor,
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            CapabilityAccess.Write,
            allowLimited: false);
        if (policy.Succeeded && capability.Allowed)
        {
            return new PrayerAdmission(actor, null);
        }

        var errorCode = capability.Allowed
            ? "forbidden"
            : capability.ErrorCode;
        try
        {
            await auditFinalizer.FinalizeOnceAsync(
                    Audit(actor, action, targetType, targetId),
                    PrivilegedAttemptOutcome.Denied,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "denied",
                        ["errorCode"] = errorCode,
                    })
                .ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return new PrayerAdmission(
                actor,
                Results.Json(
                    new
                    {
                        code = "prayer_audit_unavailable",
                        message = "The prayer audit outcome could not be persisted.",
                        correlationId = actor.CorrelationId,
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        return new PrayerAdmission(
            actor,
            Results.Json(
                new
                {
                    code = errorCode,
                    message = errorCode == "unauthenticated"
                        ? "Authentication is required."
                        : errorCode == "mfa_required"
                            ? "Multi-factor authentication is required."
                            : "Administrative access is denied.",
                },
                statusCode: errorCode == "unauthenticated"
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status403Forbidden));
    }

    private static async Task<AdministrativeRequestActor> AuthenticateActorAsync(
        HttpContext httpContext)
    {
        var result = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme)
            .ConfigureAwait(false);
        var principal = result.Succeeded && result.Principal is not null
            ? result.Principal
            : new ClaimsPrincipal(new ClaimsIdentity());
        if (principal.Identity?.IsAuthenticated == true)
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var securityStamp = principal.FindFirstValue("husaynia.security_stamp");
            var userManager = httpContext.RequestServices
                .GetRequiredService<UserManager<HusayniaIdentityUser>>();
            var user = string.IsNullOrWhiteSpace(userId)
                ? null
                : await userManager.FindByIdAsync(userId).ConfigureAwait(false);
            if (user is null ||
                string.IsNullOrWhiteSpace(securityStamp) ||
                !string.Equals(user.SecurityStamp, securityStamp, StringComparison.Ordinal))
            {
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme)
                    .ConfigureAwait(false);
                principal = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }

        httpContext.User = principal;
        var roles = principal.Claims
            .Where(claim =>
                claim.Type == ClaimTypes.Role &&
                RoleNames.All.Contains(claim.Value))
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        return new AdministrativeRequestActor(
            principal.Identity?.IsAuthenticated == true,
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            roles,
            principal.HasClaim(claim =>
                (claim.Type == "amr" &&
                 claim.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)) ||
                (claim.Type == "husaynia.mfa" &&
                 claim.Value.Equals("true", StringComparison.OrdinalIgnoreCase))),
            httpContext.RequestServices
                .GetRequiredService<ICorrelationContext>()
                .Current.CorrelationId);
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IIdentityAuditFinalizer auditFinalizer,
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string targetId)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext).ConfigureAwait(false);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            try
            {
                await auditFinalizer.FinalizeOnceAsync(
                        Audit(actor, action, targetType, targetId),
                        PrivilegedAttemptOutcome.Allowed,
                        new Dictionary<string, string?>
                        {
                            ["result"] = "transport_rejected",
                            ["errorCode"] = "antiforgery_required",
                        })
                    .ConfigureAwait(false);
            }
            catch (IdentityAuditFinalizationException)
            {
                return Results.Json(
                    new
                    {
                        code = "prayer_audit_unavailable",
                        message = "The prayer audit outcome could not be persisted.",
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Json(
                new
                {
                    code = "antiforgery_required",
                    message = "A valid antiforgery token is required.",
                    correlationId = actor.CorrelationId,
                },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> TransportFailureAsync(
        IIdentityAuditFinalizer auditFinalizer,
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string targetId,
        string errorCode,
        string message,
        ICorrelationContext correlation)
    {
        try
        {
            await auditFinalizer.FinalizeOnceAsync(
                    Audit(actor, action, targetType, targetId),
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "transport_rejected",
                        ["errorCode"] = errorCode,
                    })
                .ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return Failure(
                "prayer_audit_unavailable",
                "The prayer audit outcome could not be persisted.",
                correlation,
                StatusCodes.Status503ServiceUnavailable);
        }

        return Failure(
            errorCode,
            message,
            correlation,
            StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> InvokeAdministrationAsync<T>(
        Func<Task<Result<T, PrayerAdministrationError>>> operation,
        IIdentityAuditFinalizer auditFinalizer,
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string targetId,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToHttpResult(
                await operation().ConfigureAwait(false),
                correlation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IdentityAuditFinalizationException)
        {
            return Failure(
                "prayer_audit_unavailable",
                "The prayer audit outcome could not be persisted.",
                correlation,
                StatusCodes.Status503ServiceUnavailable);
        }
        catch
        {
            try
            {
                await auditFinalizer.FinalizeOnceAsync(
                        Audit(actor, action, targetType, targetId),
                        PrivilegedAttemptOutcome.Allowed,
                        new Dictionary<string, string?>
                        {
                            ["result"] = "exception",
                            ["errorCode"] = "unexpected_failure",
                        })
                    .ConfigureAwait(false);
            }
            catch (IdentityAuditFinalizationException)
            {
                return Failure(
                    "prayer_audit_unavailable",
                    "The prayer audit outcome could not be persisted.",
                    correlation,
                    StatusCodes.Status503ServiceUnavailable);
            }

            return Failure(
                "unexpected_failure",
                "The prayer operation failed unexpectedly.",
                correlation,
                StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<T> ReadBoundedJsonAsync<T>(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumRequestBytes)
        {
            throw new PrayerRequestException(
                "request_too_large",
                "The request body is too large.");
        }

        await using var stream = new MemoryStream();
        var buffer = new byte[4_096];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (stream.Length + read > MaximumRequestBytes)
            {
                throw new PrayerRequestException(
                    "request_too_large",
                    "The request body is too large.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        if (stream.Length == 0)
        {
            throw new PrayerRequestException(
                "invalid_request",
                "A request body is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                stream.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PrayerRequestException(
                    "invalid_request",
                    "The request body is malformed.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.EnumerateObject().Any(
                    property => !names.Add(property.Name)))
            {
                throw new PrayerRequestException(
                    "invalid_request",
                    "The request body is malformed.");
            }

            var result = JsonSerializer.Deserialize<T>(
                document.RootElement.GetRawText(),
                StrictJson);
            return result ?? throw new PrayerRequestException(
                "invalid_request",
                "The request body is malformed.");
        }
        catch (JsonException exception)
        {
            throw new PrayerRequestException(
                "invalid_request",
                "The request body is malformed.",
                exception);
        }
    }

    private static IDisposable BeginCorrelation(
        HttpContext httpContext,
        ICorrelationContext correlation,
        string operation)
    {
        var supplied = httpContext.Request.Headers["X-Correlation-ID"].ToString();
        var scope = correlation.Begin(
            string.IsNullOrWhiteSpace(supplied)
                ? httpContext.TraceIdentifier
                : supplied,
            operation);
        httpContext.Response.Headers["X-Correlation-ID"] =
            correlation.Current.CorrelationId;
        return scope;
    }

    private static IdentityAuditDescriptor Audit(
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string targetId) =>
        new(
            actor.UserId,
            actor.Roles,
            action,
            targetType,
            NormalizeTarget(targetId),
            actor.CorrelationId);

    private static string NormalizeTarget(string targetId)
    {
        var normalized = targetId.Trim();
        return normalized.Length <= 256
            ? normalized
            : "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static UserContext ToUserContext(AdministrativeRequestActor actor) =>
        new(actor.UserId ?? string.Empty, actor.Roles, actor.CorrelationId);

    private static IResult ToHttpResult<T>(
        Result<T, PrayerAdministrationError> result,
        ICorrelationContext correlation)
    {
        if (result.IsSuccess)
        {
            return Results.Ok(result.Success);
        }

        var status = result.Error.Code switch
        {
            "forbidden" or "limited_access" => StatusCodes.Status403Forbidden,
            "profile_not_found" or "override_not_found" => StatusCodes.Status404NotFound,
            "concurrency_conflict" or "profile_conflict" or "profile_not_active" or
                "override_conflict" =>
                StatusCodes.Status409Conflict,
            "timeout" or "unavailable" or "not_configured" or "persistence_failure" or
                "job_unavailable" or "prayer_audit_unavailable" =>
                StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Failure(result.Error.Code, result.Error.Message, correlation, status);
    }

    private static IResult Failure(
        string code,
        string message,
        ICorrelationContext correlation,
        int statusCode) =>
        Results.Json(
            new
            {
                code,
                message,
                correlationId = correlation.Current.CorrelationId,
            },
            statusCode: statusCode);

    private sealed record PrayerAdmission(
        AdministrativeRequestActor? Actor,
        IResult? Failure);

    private sealed class PrayerRequestException : Exception
    {
        internal PrayerRequestException(
            string code,
            string message,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code;
        }

        internal string Code { get; }
    }

    private sealed record CreateProfileRequest(
        [property: JsonPropertyName("providerKind")] string ProviderKind,
        [property: JsonPropertyName("latitude")] decimal Latitude,
        [property: JsonPropertyName("longitude")] decimal Longitude,
        [property: JsonPropertyName("methodJson")] string MethodJson,
        [property: JsonPropertyName("algorithmVersion")] string AlgorithmVersion,
        [property: JsonPropertyName("effectiveFrom")] DateOnly EffectiveFrom);

    private sealed record ActivateProfileRequest(
        [property: JsonPropertyName("expectedStateRowVersion")] byte[] ExpectedStateRowVersion);

    private sealed record SaveOverrideRequest(
        [property: JsonPropertyName("profileHash")] string ProfileHash,
        [property: JsonPropertyName("date")] DateOnly Date,
        [property: JsonPropertyName("prayerKey")] string PrayerKey,
        [property: JsonPropertyName("localTime")] TimeOnly LocalTime,
        [property: JsonPropertyName("reason")] string Reason);

    private sealed record DeactivateOverrideRequest(
        [property: JsonPropertyName("expectedRowVersion")] byte[] ExpectedRowVersion);

    private sealed record RefreshRequest(
        [property: JsonPropertyName("profileHash")] string ProfileHash,
        [property: JsonPropertyName("year")] int Year,
        [property: JsonPropertyName("month")] int Month);
}
