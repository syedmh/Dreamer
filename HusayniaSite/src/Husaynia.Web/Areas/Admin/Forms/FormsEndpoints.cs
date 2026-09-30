using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Forms;
using Husaynia.Domain.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Husaynia.Infrastructure.Identity;

namespace Husaynia.Web.Areas.Admin.Forms;

internal static class FormsEndpoints
{
    private static readonly JsonSerializerOptions RequestJsonOptions = CreateJsonOptions();
    private static readonly object ValidatedActorKey = new();

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var publicGroup = endpoints.MapGroup("/forms");
        publicGroup.MapGet("/antiforgery", GetAntiforgery);
        publicGroup.MapPost("/submissions", SubmitAsync);

        var admin = endpoints.MapGroup("/admin/forms");
        admin.MapGet("/definitions/{formKey}", GetDefinitionAsync);
        admin.MapPost("/definitions/{formKey}/publish", PublishDefinitionAsync);
        admin.MapGet("/submissions", ReadSubmissionsAsync);
        admin.MapPost("/submissions/{submissionId:guid}/delivery/retry", RetryDeliveryAsync);
        admin.MapPost("/submissions/{submissionId:guid}/retention", ChangeRetentionAsync);
    }

    private static IResult GetAntiforgery(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        FormsOptions options)
    {
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return Results.Ok(new AntiforgeryResponse(tokens.RequestToken ?? string.Empty));
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        FormsOptions options,
        FormsAdmission admission,
        IActiveFormDefinitionReader definitionReader,
        FormSubmissionValidator validator,
        IFormSubmissionService submissionService,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlation, "forms.submission");
        if (!options.Enabled)
        {
            return Results.NotFound();
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var request = await ReadRequestAsync<SubmitFormRequest>(
                httpContext.Request,
                options.MaximumRequestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.Status is not null)
        {
            return Failure(
                request.Status.Value,
                request.ErrorCode ?? "invalid_request",
                "The form request is invalid.",
                correlation);
        }

        var body = request.Value;
        if (body is null ||
            !FormSubmissionService.TryNormalizeFormKey(body.FormKey, out var formKey) ||
            body.Fields is null)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                "invalid_request",
                "The form request is invalid.",
                correlation);
        }

        if (!string.IsNullOrWhiteSpace(body.Honeypot))
        {
            return Results.Accepted(
                value: new GenericFormAcknowledgement(
                    true,
                    correlation.Current.CorrelationId));
        }

        var command = new FormSubmissionCommand(
            formKey,
            body.Fields,
            body.ConsentVersion);
        var definition = await definitionReader.GetActiveAsync(formKey, cancellationToken)
            .ConfigureAwait(false);
        if (definition.IsFailure)
        {
            return Failure(
                StatusCodes.Status404NotFound,
                "form_not_found",
                "The requested form is not available.",
                correlation);
        }

        var rateLimit = await admission.AttemptAsync(
                httpContext,
                formKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (!rateLimit.IsAllowed)
        {
            if (rateLimit.IsDependencyUnavailable)
            {
                return Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "admission_unavailable",
                    "The form request could not be accepted.",
                    correlation);
            }

            httpContext.Response.Headers.RetryAfter =
                rateLimit.RetryAfterSeconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            return Failure(
                StatusCodes.Status429TooManyRequests,
                "rate_limited",
                "The form request could not be accepted.",
                correlation);
        }

        var validation = validator.Validate(command, definition.Success);
        if (!validation.IsValid)
        {
            return Results.Json(
                new FormValidationFailure(
                    "validation_failed",
                    "One or more submitted fields are invalid.",
                    validation.Errors,
                    correlation.Current.CorrelationId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var fingerprint = admission.CreateDuplicateFingerprint(
            httpContext.Connection.RemoteIpAddress,
            formKey,
            validation.CanonicalPayloadHash);
        var result = await submissionService.SubmitAsync(
                command,
                fingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return Results.Accepted(
                value: new FormSubmissionResponse(
                    result.Success.SubmissionId,
                    result.Success.AcceptedAtUtc,
                    correlation.Current.CorrelationId));
        }

        return result.Error.Code switch
        {
            "form_not_found" => Failure(
                StatusCodes.Status404NotFound,
                result.Error.Code,
                result.Error.Message,
                correlation),
            "validation_failed" or "form_version_changed" => Failure(
                StatusCodes.Status400BadRequest,
                result.Error.Code,
                result.Error.Message,
                correlation),
            "duplicate_conflict" => Failure(
                StatusCodes.Status409Conflict,
                result.Error.Code,
                result.Error.Message,
                correlation),
            "delivery_not_configured" or "forms_audit_unavailable" => Failure(
                StatusCodes.Status503ServiceUnavailable,
                result.Error.Code,
                result.Error.Message,
                correlation),
            _ => Failure(
                StatusCodes.Status500InternalServerError,
                "forms_failure",
                "The form request could not be accepted.",
                correlation),
        };
    }

    private static Task<IResult> GetDefinitionAsync(
        HttpContext httpContext,
        string formKey,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken) =>
        ExecuteAdminBoundaryAsync(
            httpContext,
            auditFinalizer,
            FormAdministrationService.DefinitionReadAction,
            "FormDefinition",
            formKey,
            correlation,
            () => GetDefinitionCoreAsync(
                httpContext,
                formKey,
                authorizationService,
                authorizer,
                auditFinalizer,
                administration,
                correlation,
                cancellationToken));

    private static async Task<IResult> GetDefinitionCoreAsync(
        HttpContext httpContext,
        string formKey,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        var admission = await AuthorizeAdminAsync(
                httpContext,
                formKey,
                FormAdministrationService.DefinitionReadAction,
                CapabilityAccess.Read,
                authorizationService,
                authorizer,
                auditFinalizer,
                correlation)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        return await InvokeAdministrationAsync(
                () => administration.GetDefinitionAsync(
                    formKey,
                    admission.Actor!,
                    cancellationToken),
                httpContext,
                correlation,
                result => result.StateRowVersion)
            .ConfigureAwait(false);
    }

    private static Task<IResult> PublishDefinitionAsync(
        HttpContext httpContext,
        string formKey,
        IAntiforgery antiforgery,
        FormsOptions options,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken) =>
        ExecuteAdminBoundaryAsync(
            httpContext,
            auditFinalizer,
            FormAdministrationService.DefinitionPublishAction,
            "FormDefinition",
            formKey,
            correlation,
            () => PublishDefinitionCoreAsync(
                httpContext,
                formKey,
                antiforgery,
                options,
                authorizationService,
                authorizer,
                auditFinalizer,
                administration,
                correlation,
                cancellationToken));

    private static async Task<IResult> PublishDefinitionCoreAsync(
        HttpContext httpContext,
        string formKey,
        IAntiforgery antiforgery,
        FormsOptions options,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        var admission = await AuthorizeAdminAsync(
                httpContext,
                formKey,
                FormAdministrationService.DefinitionPublishAction,
                CapabilityAccess.Write,
                authorizationService,
                authorizer,
                auditFinalizer,
                correlation)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var transportFailure = await ValidateAdminTransportAsync(
                httpContext,
                antiforgery,
                options,
                auditFinalizer,
                admission.Audit!,
                cancellationToken)
            .ConfigureAwait(false);
        if (transportFailure is not null)
        {
            return transportFailure;
        }

        var request = await ReadRequestAsync<PublishDefinitionRequest>(
                httpContext.Request,
                options.MaximumRequestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.Status is not null || request.Value is null)
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    admission.Audit!,
                    request.ErrorCode ?? "invalid_request",
                    request.Status ?? StatusCodes.Status400BadRequest,
                    correlation)
                .ConfigureAwait(false);
        }

        var body = request.Value;
        if (body.Fields is null ||
            body.ExpectedStateRowVersion is null ||
            body.Fields.Any(field => field is null || field.Choices is null))
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    admission.Audit!,
                    "invalid_request",
                    StatusCodes.Status400BadRequest,
                    correlation)
                .ConfigureAwait(false);
        }

        var command = new PublishFormDefinitionCommand(
            formKey,
            body.Title,
            body.DestinationKey,
            body.TemplateKey,
            body.ConsentVersion,
            body.Fields.Select(field => new FormFieldDraft(
                field!.Key,
                field.Label,
                field.Kind,
                field.Required,
                field.MinimumLength,
                field.MaximumLength,
                field.MinimumValue,
                field.MaximumValue,
                field.PatternKind,
                field.Choices!,
                field.Order,
                field.PrivacyClass)).ToArray(),
            new RowVersion(body.ExpectedStateRowVersion));
        return await InvokeAdministrationAsync(
                () => administration.PublishAsync(
                    command,
                    admission.Actor!,
                    cancellationToken),
                httpContext,
                correlation,
                result => result.StateRowVersion)
            .ConfigureAwait(false);
    }

    private static Task<IResult> ReadSubmissionsAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken) =>
        ExecuteAdminBoundaryAsync(
            httpContext,
            auditFinalizer,
            FormAdministrationService.SubmissionReadAction,
            "FormSubmission",
            "summaries",
            correlation,
            () => ReadSubmissionsCoreAsync(
                httpContext,
                authorizationService,
                authorizer,
                auditFinalizer,
                administration,
                correlation,
                cancellationToken));

    private static async Task<IResult> ReadSubmissionsCoreAsync(
        HttpContext httpContext,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        var admission = await AuthorizeAdminAsync(
                httpContext,
                "summaries",
                FormAdministrationService.SubmissionReadAction,
                CapabilityAccess.Read,
                authorizationService,
                authorizer,
                auditFinalizer,
                correlation)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var takeText = httpContext.Request.Query["take"].FirstOrDefault();
        var take = 50;
        if (takeText is not null &&
            !int.TryParse(
                takeText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out take))
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    admission.Audit!,
                    "invalid_take",
                    StatusCodes.Status400BadRequest,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.ReadSubmissionsAsync(
                    take,
                    admission.Actor!,
                    cancellationToken),
                httpContext,
                correlation)
            .ConfigureAwait(false);
    }

    private static Task<IResult> RetryDeliveryAsync(
        HttpContext httpContext,
        Guid submissionId,
        IAntiforgery antiforgery,
        FormsOptions options,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken) =>
        ExecuteAdminBoundaryAsync(
            httpContext,
            auditFinalizer,
            FormAdministrationService.DeliveryRetryAction,
            "FormSubmission",
            submissionId.ToString("N"),
            correlation,
            () => RetryDeliveryCoreAsync(
                httpContext,
                submissionId,
                antiforgery,
                options,
                authorizationService,
                authorizer,
                auditFinalizer,
                administration,
                correlation,
                cancellationToken));

    private static async Task<IResult> RetryDeliveryCoreAsync(
        HttpContext httpContext,
        Guid submissionId,
        IAntiforgery antiforgery,
        FormsOptions options,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        var admission = await AuthorizeAdminAsync(
                httpContext,
                submissionId.ToString("N"),
                FormAdministrationService.DeliveryRetryAction,
                CapabilityAccess.Write,
                authorizationService,
                authorizer,
                auditFinalizer,
                correlation)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var transportFailure = await ValidateAdminTransportAsync(
                httpContext,
                antiforgery,
                options,
                auditFinalizer,
                admission.Audit!,
                cancellationToken)
            .ConfigureAwait(false);
        if (transportFailure is not null)
        {
            return transportFailure;
        }

        var request = await ReadRequestAsync<RetryDeliveryRequest>(
                httpContext.Request,
                options.MaximumRequestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.Status is not null || request.Value is null)
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    admission.Audit!,
                    request.ErrorCode ?? "invalid_request",
                    request.Status ?? StatusCodes.Status400BadRequest,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.RetryDeliveryAsync(
                    submissionId,
                    request.Value.Reason,
                    admission.Actor!,
                    cancellationToken),
                httpContext,
                correlation)
            .ConfigureAwait(false);
    }

    private static Task<IResult> ChangeRetentionAsync(
        HttpContext httpContext,
        Guid submissionId,
        IAntiforgery antiforgery,
        FormsOptions options,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken) =>
        ExecuteAdminBoundaryAsync(
            httpContext,
            auditFinalizer,
            FormAdministrationService.RetentionChangeAction,
            "FormSubmission",
            submissionId.ToString("N"),
            correlation,
            () => ChangeRetentionCoreAsync(
                httpContext,
                submissionId,
                antiforgery,
                options,
                authorizationService,
                authorizer,
                auditFinalizer,
                administration,
                correlation,
                cancellationToken));

    private static async Task<IResult> ChangeRetentionCoreAsync(
        HttpContext httpContext,
        Guid submissionId,
        IAntiforgery antiforgery,
        FormsOptions options,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        IFormAdministration administration,
        ICorrelationContext correlation,
        CancellationToken cancellationToken)
    {
        var admission = await AuthorizeAdminAsync(
                httpContext,
                submissionId.ToString("N"),
                FormAdministrationService.RetentionChangeAction,
                CapabilityAccess.Write,
                authorizationService,
                authorizer,
                auditFinalizer,
                correlation)
            .ConfigureAwait(false);
        if (admission.Failure is not null)
        {
            return admission.Failure;
        }

        var transportFailure = await ValidateAdminTransportAsync(
                httpContext,
                antiforgery,
                options,
                auditFinalizer,
                admission.Audit!,
                cancellationToken)
            .ConfigureAwait(false);
        if (transportFailure is not null)
        {
            return transportFailure;
        }

        var request = await ReadRequestAsync<RetentionRequest>(
                httpContext.Request,
                options.MaximumRequestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (request.Status is not null || request.Value is null)
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    admission.Audit!,
                    request.ErrorCode ?? "invalid_request",
                    request.Status ?? StatusCodes.Status400BadRequest,
                    correlation)
                .ConfigureAwait(false);
        }

        if (request.Value.ExpectedStateRowVersion is null)
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    admission.Audit!,
                    "invalid_request",
                    StatusCodes.Status400BadRequest,
                    correlation)
                .ConfigureAwait(false);
        }

        return await InvokeAdministrationAsync(
                () => administration.ChangeRetentionAsync(
                    new FormRetentionCommand(
                        submissionId,
                        request.Value.Action,
                        new RowVersion(request.Value.ExpectedStateRowVersion)),
                    admission.Actor!,
                    cancellationToken),
                httpContext,
                correlation,
                result => result.StateRowVersion)
            .ConfigureAwait(false);
    }

    private static async Task<AdminAdmission> AuthorizeAdminAsync(
        HttpContext httpContext,
        string targetId,
        string action,
        CapabilityAccess access,
        IAuthorizationService authorizationService,
        AdministrativeCapabilityAuthorizer authorizer,
        IIdentityAuditFinalizer auditFinalizer,
        ICorrelationContext correlation)
    {
        var actor = await AuthenticateActorAsync(
                httpContext,
                correlation.Current.CorrelationId)
            .ConfigureAwait(false);
        var audit = new IdentityAuditDescriptor(
            actor.UserId,
            actor.Roles,
            action,
            action == FormAdministrationService.DefinitionReadAction ||
            action == FormAdministrationService.DefinitionPublishAction
                ? "FormDefinition"
                : "FormSubmission",
            NormalizeTargetId(targetId),
            actor.CorrelationId);
        var policy = await authorizationService.AuthorizeAsync(
                httpContext.User,
                PolicyNames.SiteAdministration)
            .ConfigureAwait(false);
        var evaluation = authorizer.Authorize(
            actor,
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            access,
            allowLimited: false);
        var allowed = policy.Succeeded &&
            evaluation.Allowed &&
            actor.Roles.Contains(RoleNames.SiteAdministrator);
        if (allowed)
        {
            return new AdminAdmission(actor, audit, null);
        }

        var code = !evaluation.Allowed
            ? evaluation.ErrorCode
            : actor.IsAuthenticated ? "forbidden" : "unauthenticated";
        try
        {
            await auditFinalizer.FinalizeOnceAsync(
                audit,
                PrivilegedAttemptOutcome.Denied,
                new Dictionary<string, string?>
                {
                    ["errorCode"] = code,
                    ["result"] = "denied",
                }).ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return new AdminAdmission(
                null,
                null,
                Failure(
                    StatusCodes.Status503ServiceUnavailable,
                    "forms_audit_unavailable",
                    "The Forms authorization outcome could not be persisted.",
                    correlation));
        }

        return new AdminAdmission(
            null,
            null,
            Failure(
                actor.IsAuthenticated
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status401Unauthorized,
                code,
                actor.IsAuthenticated
                    ? "The signed-in user is not authorized for Forms administration."
                    : "Authentication is required for Forms administration.",
                correlation));
    }

    private static async Task<IResult> ExecuteAdminBoundaryAsync(
        HttpContext httpContext,
        IIdentityAuditFinalizer auditFinalizer,
        string action,
        string targetType,
        string targetId,
        ICorrelationContext correlation,
        Func<Task<IResult>> operation)
    {
        using var _ = BeginCorrelation(httpContext, correlation, action);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return Failure(
                StatusCodes.Status503ServiceUnavailable,
                "forms_audit_unavailable",
                "The Forms audit outcome could not be persisted.",
                correlation);
        }
        catch (OperationCanceledException)
        {
            return await FinalizeBoundaryFailureAsync(
                    httpContext,
                    auditFinalizer,
                    action,
                    targetType,
                    targetId,
                    "cancelled",
                    "request_cancelled",
                    499,
                    "The Forms operation was cancelled.",
                    correlation)
                .ConfigureAwait(false);
        }
        catch
        {
            return await FinalizeBoundaryFailureAsync(
                    httpContext,
                    auditFinalizer,
                    action,
                    targetType,
                    targetId,
                    "exception",
                    "unexpected_failure",
                    StatusCodes.Status500InternalServerError,
                    "The Forms operation failed unexpectedly.",
                    correlation)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> FinalizeBoundaryFailureAsync(
        HttpContext httpContext,
        IIdentityAuditFinalizer auditFinalizer,
        string action,
        string targetType,
        string targetId,
        string result,
        string errorCode,
        int statusCode,
        string message,
        ICorrelationContext correlation)
    {
        var actor = httpContext.Items.TryGetValue(ValidatedActorKey, out var actorValue) &&
            actorValue is AdministrativeRequestActor validatedActor
                ? validatedActor
                : CreateActor(
                    new ClaimsPrincipal(new ClaimsIdentity()),
                    correlation.Current.CorrelationId);
        try
        {
            await auditFinalizer.FinalizeOnceAsync(
                    new IdentityAuditDescriptor(
                        actor.UserId,
                        actor.Roles,
                        action,
                        targetType,
                        NormalizeTargetId(targetId),
                        actor.CorrelationId),
                    actor.IsAuthenticated
                        ? PrivilegedAttemptOutcome.Allowed
                        : PrivilegedAttemptOutcome.Denied,
                    new Dictionary<string, string?>
                    {
                        ["result"] = result,
                        ["errorCode"] = errorCode,
                    })
                .ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return Failure(
                StatusCodes.Status503ServiceUnavailable,
                "forms_audit_unavailable",
                "The Forms audit outcome could not be persisted.",
                correlation);
        }

        return Failure(statusCode, errorCode, message, correlation);
    }

    private static async Task<IResult?> ValidateAdminTransportAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        FormsOptions options,
        IIdentityAuditFinalizer auditFinalizer,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.ContentLength > options.MaximumRequestBytes)
        {
            return await AuditTransportFailureAsync(
                    auditFinalizer,
                    audit,
                    "request_too_large",
                    StatusCodes.Status413PayloadTooLarge,
                    httpContext.RequestServices.GetRequiredService<ICorrelationContext>())
                .ConfigureAwait(false);
        }

        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is null)
        {
            return null;
        }

        await auditFinalizer.FinalizeOnceAsync(
            audit,
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "antiforgery_failed",
                ["result"] = "invalid_transport",
            }).ConfigureAwait(false);
        return antiforgeryFailure;
    }

    private static async Task<IResult> AuditTransportFailureAsync(
        IIdentityAuditFinalizer auditFinalizer,
        IdentityAuditDescriptor audit,
        string errorCode,
        int status,
        ICorrelationContext correlation)
    {
        await auditFinalizer.FinalizeOnceAsync(
            audit,
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = errorCode,
                ["result"] = "invalid_transport",
            }).ConfigureAwait(false);
        return Failure(
            status,
            errorCode,
            "The Forms administration request is invalid.",
            correlation);
    }

    private static AdministrativeRequestActor CreateActor(
        ClaimsPrincipal principal,
        string correlationId)
    {
        var roles = principal.Claims
            .Where(claim => claim.Type == ClaimTypes.Role && RoleNames.All.Contains(claim.Value))
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        var mfa = principal.HasClaim(claim =>
            claim.Type == "amr" &&
            claim.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase) ||
            claim.Type == "husaynia.mfa" &&
            claim.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        return new AdministrativeRequestActor(
            principal.Identity?.IsAuthenticated == true,
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            roles,
            mfa,
            correlationId);
    }

    private static async Task<AdministrativeRequestActor> AuthenticateActorAsync(
        HttpContext httpContext,
        string correlationId)
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
            var stale = user is null ||
                user.IsDisabled ||
                string.IsNullOrWhiteSpace(securityStamp) ||
                !string.Equals(user.SecurityStamp, securityStamp, StringComparison.Ordinal);
            if (!stale)
            {
                var ticketRoles = principal.Claims
                    .Where(claim =>
                        claim.Type == ClaimTypes.Role &&
                        RoleNames.All.Contains(claim.Value))
                    .Select(claim => claim.Value)
                    .ToHashSet(StringComparer.Ordinal);
                var currentRoles = (await userManager.GetRolesAsync(user!).ConfigureAwait(false))
                    .Where(RoleNames.All.Contains)
                    .ToHashSet(StringComparer.Ordinal);
                var hasMfaClaim = principal.HasClaim(claim =>
                    (claim.Type == "amr" &&
                     claim.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)) ||
                    (claim.Type == "husaynia.mfa" &&
                     claim.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));
                stale = !ticketRoles.SetEquals(currentRoles) ||
                    hasMfaClaim && !user!.TwoFactorEnabled;
            }

            if (stale)
            {
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme)
                    .ConfigureAwait(false);
                principal = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }

        httpContext.User = principal;
        var actor = CreateActor(principal, correlationId);
        httpContext.Items[ValidatedActorKey] = actor;
        return actor;
    }

    private static string? NormalizeTargetId(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        var normalized = targetId.Trim();
        return normalized.Length <= 256
            ? normalized
            : "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

    private static IResult MapAdminResult<T>(
        Result<T, FormError> result,
        ICorrelationContext correlation)
    {
        if (result.IsSuccess)
        {
            return Results.Ok(result.Success);
        }

        var status = result.Error.Code switch
        {
            "unauthenticated" => StatusCodes.Status401Unauthorized,
            "forbidden" or "mfa_required" => StatusCodes.Status403Forbidden,
            "form_not_found" or "submission_not_found" =>
                StatusCodes.Status404NotFound,
            "definition_conflict" or "retention_conflict" =>
                StatusCodes.Status409Conflict,
            "forms_persistence_failure" or
            "forms_audit_unavailable" or
            "delivery_retry_unavailable" =>
                StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Failure(status, result.Error.Code, result.Error.Message, correlation);
    }

    private static async Task<IResult> InvokeAdministrationAsync<T>(
        Func<Task<Result<T, FormError>>> operation,
        HttpContext httpContext,
        ICorrelationContext correlation,
        Func<T, RowVersion>? stateRowVersion = null)
    {
        var result = await operation().ConfigureAwait(false);
        if (result.IsSuccess && stateRowVersion is not null)
        {
            httpContext.Response.Headers.ETag =
                ToEntityTag(stateRowVersion(result.Success));
        }

        return MapAdminResult(result, correlation);
    }

    private static string ToEntityTag(RowVersion rowVersion) =>
        $"\"{Convert.ToBase64String(rowVersion.Value.Span)}\"";

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext).ConfigureAwait(false);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Json(
                new FailureResponse(
                    "antiforgery_failed",
                    "A valid antiforgery token is required.",
                    httpContext.TraceIdentifier),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<RequestReadResult<T>> ReadRequestAsync<T>(
        HttpRequest request,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > maximumBytes)
        {
            return RequestReadResult<T>.Failure(
                StatusCodes.Status413PayloadTooLarge,
                "request_too_large");
        }

        try
        {
            await using var buffer = new MemoryStream();
            var chunk = new byte[4_096];
            while (true)
            {
                var read = await request.Body.ReadAsync(chunk, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumBytes)
                {
                    return RequestReadResult<T>.Failure(
                        StatusCodes.Status413PayloadTooLarge,
                        "request_too_large");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            using var document = JsonDocument.Parse(
                buffer.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                HasDuplicateObjectProperties(document.RootElement))
            {
                return RequestReadResult<T>.Failure(
                    StatusCodes.Status400BadRequest,
                    "invalid_json");
            }

            var value = document.RootElement.Deserialize<T>(RequestJsonOptions);
            return value is null
                ? RequestReadResult<T>.Failure(
                    StatusCodes.Status400BadRequest,
                    "invalid_json")
                : RequestReadResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return RequestReadResult<T>.Failure(
                StatusCodes.Status400BadRequest,
                "invalid_json");
        }
    }

    private static bool HasDuplicateObjectProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) ||
                        HasDuplicateObjectProperties(property.Value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasDuplicateObjectProperties(item))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static IDisposable BeginCorrelation(
        HttpContext httpContext,
        ICorrelationContext correlation,
        string operation)
    {
        var supplied = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault() ??
            httpContext.TraceIdentifier;
        var scope = correlation.Begin(supplied, operation);
        httpContext.Response.Headers["X-Correlation-ID"] =
            correlation.Current.CorrelationId;
        return scope;
    }

    private static IResult Failure(
        int status,
        string code,
        string message,
        ICorrelationContext correlation) =>
        Results.Json(
            new FailureResponse(
                code,
                message,
                correlation.Current.CorrelationId),
            statusCode: status);

    private sealed record AdminAdmission(
        AdministrativeRequestActor? Actor,
        IdentityAuditDescriptor? Audit,
        IResult? Failure);

    private sealed record RequestReadResult<T>(
        T? Value,
        int? Status,
        string? ErrorCode)
    {
        internal static RequestReadResult<T> Success(T value) => new(value, null, null);

        internal static RequestReadResult<T> Failure(int status, string code) =>
            new(default, status, code);
    }

    private sealed record AntiforgeryResponse(string RequestToken);

    private sealed record SubmitFormRequest(
        [property: JsonRequired] string FormKey,
        [property: JsonRequired] Dictionary<string, string> Fields,
        string? ConsentVersion,
        string? Honeypot);

    private sealed record PublishDefinitionRequest(
        [property: JsonRequired] string Title,
        [property: JsonRequired] string DestinationKey,
        [property: JsonRequired] string TemplateKey,
        string? ConsentVersion,
        [property: JsonRequired] IReadOnlyList<FormFieldRequest?> Fields,
        [property: JsonRequired] byte[] ExpectedStateRowVersion);

    private sealed record FormFieldRequest(
        [property: JsonRequired] string Key,
        [property: JsonRequired] string Label,
        [property: JsonRequired] FormFieldKind Kind,
        [property: JsonRequired] bool Required,
        [property: JsonRequired] int? MinimumLength,
        [property: JsonRequired] int? MaximumLength,
        [property: JsonRequired] decimal? MinimumValue,
        [property: JsonRequired] decimal? MaximumValue,
        [property: JsonRequired] FormPatternKind PatternKind,
        [property: JsonRequired] IReadOnlyList<string> Choices,
        [property: JsonRequired] int Order,
        [property: JsonRequired] FormPrivacyClass PrivacyClass);

    private sealed record RetryDeliveryRequest(
        [property: JsonRequired] string Reason);

    private sealed record RetentionRequest(
        [property: JsonRequired] FormRetentionAction Action,
        [property: JsonRequired] byte[] ExpectedStateRowVersion);

    private sealed record GenericFormAcknowledgement(bool Accepted, string CorrelationId);

    private sealed record FormSubmissionResponse(
        Guid SubmissionId,
        DateTimeOffset AcceptedAtUtc,
        string CorrelationId);

    private sealed record FormValidationFailure(
        string Code,
        string Message,
        IReadOnlyList<FormValidationError> Errors,
        string CorrelationId);

    private sealed record FailureResponse(
        string Code,
        string Message,
        string CorrelationId);
}
