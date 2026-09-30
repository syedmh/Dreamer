using System.Security.Claims;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Husaynia.Web.Areas.Admin.Identity;

internal sealed record IdentityPrivilegedEndpointMetadata(
    AdministrativeCapability Capability,
    string Action,
    string TargetType,
    Func<HttpContext, string?> ResolveTargetId);

internal sealed class IdentityAuthorizationMiddlewareResultHandler
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var metadata = context.GetEndpoint()?.Metadata
            .GetMetadata<IdentityPrivilegedEndpointMetadata>();
        if (metadata is not null && !authorizeResult.Succeeded)
        {
            var correlationContext = context.RequestServices.GetRequiredService<ICorrelationContext>();
            using var correlationScope = correlationContext.Begin(
                context.Request.Headers["X-Correlation-ID"].ToString(),
                metadata.Action);
            var roles = context.User.Claims
                .Where(claim => claim.Type == ClaimTypes.Role && RoleNames.All.Contains(claim.Value))
                .Select(claim => claim.Value)
                .ToHashSet(StringComparer.Ordinal);
            var actor = new AdministrativeRequestActor(
                context.User.Identity?.IsAuthenticated == true,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                roles,
                IdentityAdminEndpoints.HasSatisfiedMfa(context.User),
                correlationContext.Current.CorrelationId);
            var authorizer = context.RequestServices
                .GetRequiredService<AdministrativeCapabilityAuthorizer>();
            var evaluation = authorizer.CanEnterCapability(
                actor,
                metadata.Capability);
            var errorCode = evaluation.Allowed
                ? authorizeResult.Challenged ? "unauthenticated" : "forbidden"
                : evaluation.ErrorCode;
            var message = evaluation.Allowed
                ? authorizeResult.Challenged
                    ? "Authentication is required to access administration."
                    : "Administrative access is denied."
                : evaluation.Message;
            var auditFinalizer = context.RequestServices
                .GetRequiredService<IIdentityAuditFinalizer>();
            try
            {
                await auditFinalizer.FinalizeOnceAsync(
                        new IdentityAuditDescriptor(
                            actor.UserId,
                            actor.Roles,
                            metadata.Action,
                            metadata.TargetType,
                            IdentityAdminEndpoints.NormalizeAuditTargetId(metadata.ResolveTargetId(context)),
                            actor.CorrelationId),
                        PrivilegedAttemptOutcome.Denied,
                        new Dictionary<string, string?>
                        {
                            ["errorCode"] = errorCode,
                            ["message"] = message,
                            ["capability"] = metadata.Capability.ToString(),
                        })
                    .ConfigureAwait(false);
            }
            catch (IdentityAuditFinalizationException)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "identity_audit_unavailable",
                    message = "The identity service is temporarily unavailable.",
                    correlationId = actor.CorrelationId,
                }).ConfigureAwait(false);
                return;
            }
        }

        await fallback.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}
