using System.Text.Json;
using HusayniaTabruk.Infrastructure.Identity.Services;
using Microsoft.AspNetCore.Routing;

namespace HusayniaTabruk.Api.ContractTests.Conventions;

public sealed class AdminEndpointContractTests
{
    [Theory]
    [InlineData("http://trusted.example/admin")]
    [InlineData("ftp://trusted.example/admin")]
    [InlineData("https://user:password@trusted.example/admin")]
    [InlineData("https://trusted.example/admin?token=value")]
    [InlineData("https://trusted.example/admin#fragment")]
    public void InvitationBaseUrlRejectsInsecureOrAmbiguousUrls(string value)
    {
        TabrukAuthOptions options = new()
        {
            InvitationBaseUrl = value,
        };

        Assert.Throws<InvalidOperationException>(options.GetInvitationBaseUri);
    }

    [Fact]
    public async Task RouteTableAndOpenApiDoNotExposeGeneralRoleEndpoints()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(environmentName: "Production");

        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/admin/members", out _));
        Assert.True(paths.TryGetProperty("/admin/invitations", out _));
        Assert.True(paths.TryGetProperty("/admin/members/{id}/food-incharge", out _));
        Assert.True(paths.TryGetProperty("/admin/admin-role-requests", out _));
        Assert.True(paths.TryGetProperty("/admin/admin-role-requests/{id}/approve", out _));
        Assert.True(paths.TryGetProperty("/admin/members/{id}/disable", out _));
        Assert.False(paths.TryGetProperty("/admin/roles", out _));
        Assert.False(paths.TryGetProperty("/admin/members/{id}/roles", out _));
        Assert.False(paths.TryGetProperty("/admin/members/{id}/admin", out _));

        RouteEndpoint[] adminRoutes = api.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith("/api/v1/admin", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.DoesNotContain(
            adminRoutes,
            endpoint => endpoint.RoutePattern.RawText is
                "/api/v1/admin/roles"
                or "/api/v1/admin/members/{id}/roles"
                or "/api/v1/admin/members/{id}/admin");
    }

    [Fact]
    public async Task OpenApiIncludesConcreteAdminBodiesHeadersAndResponseSchemas()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(environmentName: "Production");

        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;
        JsonElement components = root.GetProperty("components");
        JsonElement paths = root.GetProperty("paths");

        JsonElement invitationPost = paths.GetProperty("/admin/invitations").GetProperty("post");
        Assert.Equal(
            "#/components/schemas/IssueInvitationRequest",
            GetRequestSchemaReference(invitationPost));
        Assert.Equal(
            "#/components/schemas/IssueInvitationResponse",
            GetResponseSchemaReference(invitationPost, "200"));
        AssertRequiredProperties(
            components,
            "IssueInvitationRequest",
            "email",
            "expiresInHours");
        Assert.Contains(
            GetParameterReferences(invitationPost),
            reference => reference == "#/components/parameters/stepUpToken");

        JsonElement listMembersGet = paths.GetProperty("/admin/members").GetProperty("get");
        Assert.Contains(
            GetParameterReferences(listMembersGet),
            reference => reference == "#/components/parameters/cursor");
        Assert.Contains(
            GetParameterReferences(listMembersGet),
            reference => reference == "#/components/parameters/pageSize");
        Assert.Equal(
            "#/components/schemas/CursorPageOfAdminMemberResponse",
            GetResponseSchemaReference(listMembersGet, "200"));
        AssertRequiredResponseHeader(listMembersGet, "200", "ETag");

        JsonElement disablePost = paths.GetProperty("/admin/members/{id}/disable").GetProperty("post");
        Assert.Equal(
            "#/components/schemas/ReasonRequest",
            GetRequestSchemaReference(disablePost));
        Assert.Equal(
            "#/components/schemas/AdminMemberResponse",
            GetResponseSchemaReference(disablePost, "200"));
        Assert.Contains(
            GetParameterReferences(disablePost),
            reference => reference == "#/components/parameters/ifMatch");
        Assert.Contains(
            GetParameterReferences(disablePost),
            reference => reference == "#/components/parameters/stepUpToken");
        AssertRequiredResponseHeader(disablePost, "200", "ETag");

        JsonElement approvePost = paths.GetProperty("/admin/admin-role-requests/{id}/approve").GetProperty("post");
        Assert.Equal(
            "#/components/schemas/ReasonRequest",
            GetRequestSchemaReference(approvePost));
        Assert.Equal(
            "#/components/schemas/AdminRoleChangeRequestResponse",
            GetResponseSchemaReference(approvePost, "200"));
        AssertRequiredResponseHeader(approvePost, "200", "ETag");

        JsonElement proposePost = paths.GetProperty("/admin/admin-role-requests").GetProperty("post");
        AssertRequiredProperties(
            components,
            "ProposeRoleChangeRequest",
            "targetMembershipId",
            "action",
            "reason");
        AssertRequiredResponseHeader(proposePost, "200", "ETag");

        AssertRequiredProperties(components, "ReasonRequest", "reason");
        JsonElement foodIncharge = paths.GetProperty("/admin/members/{id}/food-incharge");
        AssertRequiredResponseHeader(foodIncharge.GetProperty("put"), "204", "ETag");
        AssertRequiredResponseHeader(foodIncharge.GetProperty("delete"), "204", "ETag");

        JsonElement stepUpParameter = components.GetProperty("parameters").GetProperty("stepUpToken");
        Assert.Equal("X-Step-Up-Token", stepUpParameter.GetProperty("name").GetString());
        Assert.Equal("header", stepUpParameter.GetProperty("in").GetString());
        Assert.True(stepUpParameter.GetProperty("required").GetBoolean());

        JsonElement invitationResponseSchema = components
            .GetProperty("schemas")
            .GetProperty("IssueInvitationResponse");
        Assert.Equal(
            "string",
            invitationResponseSchema
                .GetProperty("properties")
                .GetProperty("inviteUrl")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            "date-time",
            invitationResponseSchema
                .GetProperty("properties")
                .GetProperty("expiresAt")
                .GetProperty("format")
                .GetString());

        JsonElement adminMemberSchema = components
            .GetProperty("schemas")
            .GetProperty("AdminMemberResponse");
        Assert.False(adminMemberSchema.GetProperty("properties").TryGetProperty("email", out _));
        Assert.False(adminMemberSchema.GetProperty("properties").TryGetProperty("userId", out _));
        Assert.False(adminMemberSchema.GetProperty("properties").TryGetProperty("organizationId", out _));
    }

    private static string GetRequestSchemaReference(JsonElement operation) =>
        operation
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString()
        ?? throw new InvalidOperationException("The OpenAPI request body schema reference was missing.");

    private static string GetResponseSchemaReference(JsonElement operation, string statusCode) =>
        operation
            .GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString()
        ?? throw new InvalidOperationException("The OpenAPI response schema reference was missing.");

    private static string[] GetParameterReferences(JsonElement operation)
    {
        if (!operation.TryGetProperty("parameters", out JsonElement parameters))
        {
            return [];
        }

        return parameters
            .EnumerateArray()
            .Select(parameter => parameter.TryGetProperty("$ref", out JsonElement referenceProperty)
                ? referenceProperty.GetString()
                : null)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Cast<string>()
            .ToArray();
    }

    private static void AssertRequiredProperties(
        JsonElement components,
        string schemaName,
        params string[] expectedProperties)
    {
        string[] required = components
            .GetProperty("schemas")
            .GetProperty(schemaName)
            .GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString())
            .Where(property => property is not null)
            .Cast<string>()
            .ToArray();

        Assert.Equal(expectedProperties, required);
    }

    private static void AssertRequiredResponseHeader(
        JsonElement operation,
        string statusCode,
        string headerName)
    {
        JsonElement header = operation
            .GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("headers")
            .GetProperty(headerName);

        Assert.True(header.GetProperty("required").GetBoolean());
        Assert.Equal("string", header.GetProperty("schema").GetProperty("type").GetString());
    }
}
