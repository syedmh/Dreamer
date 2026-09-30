using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.ContractTests.Conventions;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Signups;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace HusayniaTabruk.Api.ContractTests.Privacy;

public sealed class SignupRosterSnapshots
{
    private static readonly string[] ExpectedRequestProperties =
    [
        "kind",
        "label",
        "memberParticipantIds",
        "unnamedParticipantCount",
    ];

    private static readonly string?[] ExpectedLabelTypes =
    [
        "string",
        "null",
    ];

    private static readonly string[] ExpectedResponseProperties =
    [
        "category",
        "helpNeedId",
        "id",
        "kind",
        "label",
        "lastTransitionAt",
        "memberParticipants",
        "primaryContact",
        "serviceDateId",
        "signupVersion",
        "status",
        "submittedAt",
        "totalParticipantCount",
        "unnamedParticipantCount",
        "version",
        "waitlistOrder",
    ];

    [Fact]
    public async Task OpenApiFreezesT12PathsInputAndPrivacySafeProjection()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document = JsonDocument.Parse(
            await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;
        JsonElement paths = root.GetProperty("paths");

        JsonElement submit = paths
            .GetProperty("/needs/{needId}/signups")
            .GetProperty("post");
        JsonElement mine = paths
            .GetProperty("/signups/mine")
            .GetProperty("get");
        JsonElement roster = paths
            .GetProperty("/dates/{dateId}/roster")
            .GetProperty("get");

        Assert.Equal("SubmitSignup", submit.GetProperty("operationId").GetString());
        Assert.Equal("ListMySignups", mine.GetProperty("operationId").GetString());
        Assert.Equal("GetManagedRoster", roster.GetProperty("operationId").GetString());
        Assert.True(submit.GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(submit.GetProperty("responses").TryGetProperty("413", out _));
        JsonElement rateLimited =
            submit.GetProperty("responses").GetProperty("429");
        JsonElement retryAfter = rateLimited
            .GetProperty("headers")
            .GetProperty(ApiDefaults.RetryAfterHeaderName);
        Assert.True(retryAfter.GetProperty("required").GetBoolean());
        Assert.Equal(
            "integer",
            retryAfter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(
            1,
            retryAfter.GetProperty("schema").GetProperty("minimum").GetInt32());

        JsonElement requestSchema = ResolveSchema(
            root,
            submit.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        Assert.Equal(
            ExpectedRequestProperties,
            requestSchema.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        JsonElement requestProperties = requestSchema.GetProperty("properties");
        JsonElement label = requestProperties.GetProperty("label");
        Assert.Equal(
            ExpectedLabelTypes,
            label.GetProperty("type")
                .EnumerateArray()
                .Select(type => type.GetString())
                .ToArray());
        Assert.DoesNotContain(
            "label",
            requestSchema.GetProperty("required")
                .EnumerateArray()
                .Select(property => property.GetString()));
        Assert.Equal(
            ApplicationLimits.MaximumSignupLabelUnicodeScalars,
            label.GetProperty("maxLength").GetInt32());
        Assert.Equal(
            ApplicationLimits.MaximumSignupLabelUtf8Bytes,
            label.GetProperty("x-maxUtf8Bytes").GetInt32());
        Assert.Equal(
            SignupLabelPolicy.OpenApiPattern,
            label.GetProperty("pattern").GetString());
        Assert.Contains(
            "cannot establish real-world anonymity",
            label.GetProperty("description").GetString(),
            StringComparison.Ordinal);

        JsonElement memberParticipantIds =
            requestProperties.GetProperty("memberParticipantIds");
        Assert.Equal(
            ApplicationLimits.MaximumNamedParticipants,
            memberParticipantIds.GetProperty("maxItems").GetInt32());
        Assert.True(memberParticipantIds.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal(
            "uuid",
            memberParticipantIds.GetProperty("items").GetProperty("format").GetString());

        JsonElement unnamedParticipantCount =
            requestProperties.GetProperty("unnamedParticipantCount");
        Assert.Equal(0, unnamedParticipantCount.GetProperty("minimum").GetInt32());
        Assert.Equal(
            ApplicationLimits.MaximumUnnamedParticipants,
            unnamedParticipantCount.GetProperty("maximum").GetInt32());
        Assert.Equal(
            1,
            requestSchema.GetProperty("x-minimumTotalParticipants").GetInt32());
        Assert.Equal(
            ApplicationLimits.MaximumTotalParticipants,
            requestSchema.GetProperty("x-maximumTotalParticipants").GetInt32());

        JsonElement signupSchema = ResolveSchema(
            root,
            submit.GetProperty("responses")
                .GetProperty("201")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        string[] responseProperties = signupSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ExpectedResponseProperties,
            responseProperties);
        Assert.DoesNotContain(
            responseProperties,
            property => ContainsPrivateIdentityName(property));
        Assert.DoesNotContain(
            requestSchema.GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name),
            property => property.Contains(
                "participantName",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimeLabelPolicyMatchesThePublishedOpenApiBoundaryContract()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document = JsonDocument.Parse(
            await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;
        JsonElement submit = root.GetProperty("paths")
            .GetProperty("/needs/{needId}/signups")
            .GetProperty("post");
        JsonElement requestSchema = ResolveSchema(
            root,
            submit.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        JsonElement labelSchema = requestSchema
            .GetProperty("properties")
            .GetProperty("label");
        int maximumScalars = labelSchema.GetProperty("maxLength").GetInt32();
        int maximumUtf8Bytes = labelSchema.GetProperty("x-maxUtf8Bytes").GetInt32();
        string pattern = labelSchema.GetProperty("pattern").GetString()!;
        string maximumCanonicalLabel = string.Join(
            ' ',
            Enumerable.Repeat("Food", 15).Append("Group"));
        string?[] values =
        [
            null,
            "",
            " ",
            "   ",
            " Food",
            "Food ",
            "Food  Team",
            "Food\n",
            "Food\r",
            "Food\r\n",
            "Food\u2028",
            "Food\u2029",
            "FoodX",
            new string(' ', maximumScalars + 1) + "Food",
            "Food",
            "Food Preparation Group 2",
            maximumCanonicalLabel,
        ];

        foreach (string? value in values)
        {
            bool openApiAccepts = value is null;
            if (value is not null)
            {
                openApiAccepts =
                    value.EnumerateRunes().Count() <= maximumScalars
                    && Encoding.UTF8.GetByteCount(value) <= maximumUtf8Bytes
                    && Regex.IsMatch(
                        value,
                        pattern,
                        RegexOptions.CultureInvariant);
            }

            SignupLabelValidationResult runtime =
                SignupLabelPolicy.Normalize(value);

            Assert.Equal(openApiAccepts, runtime.IsSuccess);
            if (runtime.IsSuccess)
            {
                Assert.Equal(value, runtime.Value);
            }
        }
    }

    [Fact]
    public async Task SubmissionEndpointFreezesBodyAndAccountOrganizationQuotas()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        RouteEndpoint endpoint = Assert.Single(
            api.Endpoints.OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText
                == $"{ApiDefaults.BasePath}/needs/{{needId}}/signups");
        RequestBodyLimitMetadata bodyLimit =
            endpoint.Metadata.GetMetadata<RequestBodyLimitMetadata>()!;
        ApiRateLimitMetadata rateLimit =
            endpoint.Metadata.GetMetadata<ApiRateLimitMetadata>()!;

        Assert.Equal(ApplicationLimits.MaximumSignupRequestBytes, bodyLimit.MaximumBytes);
        Assert.Collection(
            rateLimit.Rules,
            account =>
            {
                Assert.Equal(ApiRateLimitPartitions.Account, account.Partition);
                Assert.Equal(
                    ApplicationLimits.SignupSubmissionsPerMinutePerAccount,
                    account.PermitLimit);
                Assert.Equal(TimeSpan.FromMinutes(1), account.Window);
            },
            organization =>
            {
                Assert.Equal(
                    ApiRateLimitPartitions.Organization,
                    organization.Partition);
                Assert.Equal(
                    ApplicationLimits.SignupSubmissionsPerHourPerOrganization,
                    organization.PermitLimit);
                Assert.Equal(TimeSpan.FromHours(1), organization.Window);
            });

        ApiRateLimitStore store = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RateLimitRule accountRule = rateLimit.Rules[0];
        RateLimitRule organizationRule = rateLimit.Rules[1];
        for (int request = 0;
             request < ApplicationLimits.SignupSubmissionsPerHourPerOrganization;
             request++)
        {
            Assert.Null(
                store.TryAcquire(
                    [
                        ($"account-{request}", accountRule),
                        ("organization-a", organizationRule),
                    ],
                    now));
        }

        Assert.NotNull(
            store.TryAcquire(
                [
                    ("account-over-organization-quota", accountRule),
                    ("organization-a", organizationRule),
                ],
                now));
    }

    private static JsonElement ResolveSchema(
        JsonElement root,
        JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out JsonElement reference))
        {
            return schema;
        }

        const string prefix = "#/components/schemas/";
        string value = reference.GetString()!;
        Assert.StartsWith(prefix, value, StringComparison.Ordinal);
        return root.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(value[prefix.Length..]);
    }

    private static bool ContainsPrivateIdentityName(string propertyName) =>
        propertyName.Contains("email", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("phone", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("login", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("token", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("userId", StringComparison.OrdinalIgnoreCase)
        || propertyName.Contains("participantName", StringComparison.OrdinalIgnoreCase);
}
