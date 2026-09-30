using System.Text.Json;
using HusayniaTabruk.Api.ContractTests.Conventions;
using HusayniaTabruk.Api.Endpoints.V1.Members;

namespace HusayniaTabruk.Api.ContractTests.Privacy;

public sealed class EligibleParticipantPrivacyContractTests
{
    [Fact]
    public async Task EligibleParticipantItemHasExactlyTheTwoMinimizedFields()
    {
        Assert.Equal(
            new[]
            {
                nameof(EligibleSignupParticipantResponse.MembershipId),
                nameof(EligibleSignupParticipantResponse.DisplayName),
            },
            typeof(EligibleSignupParticipantResponse)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());

        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document = JsonDocument.Parse(
            await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;
        JsonElement operation = root.GetProperty("paths")
            .GetProperty("/members/eligible-participants")
            .GetProperty("get");
        JsonElement page = ResolveSchema(
            root,
            operation.GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema"));
        JsonElement item = ResolveSchema(
            root,
            page.GetProperty("properties")
                .GetProperty("items")
                .GetProperty("items"));
        string[] properties = item.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["displayName", "membershipId"], properties);
        Assert.Equal(
            "uuid",
            item.GetProperty("properties")
                .GetProperty("membershipId")
                .GetProperty("format")
                .GetString());
        Assert.DoesNotContain(
            properties,
            property =>
                property.Contains("user", StringComparison.OrdinalIgnoreCase)
                || property.Contains("email", StringComparison.OrdinalIgnoreCase)
                || property.Contains("phone", StringComparison.OrdinalIgnoreCase)
                || property.Contains("role", StringComparison.OrdinalIgnoreCase)
                || property.Contains("status", StringComparison.OrdinalIgnoreCase)
                || property.Contains("invitation", StringComparison.OrdinalIgnoreCase)
                || property.Contains("eligible", StringComparison.OrdinalIgnoreCase)
                || property.Contains("signup", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement ResolveSchema(
        JsonElement root,
        JsonElement schema)
    {
        const string prefix = "#/components/schemas/";
        string reference = schema.GetProperty("$ref").GetString()!;
        Assert.StartsWith(prefix, reference, StringComparison.Ordinal);
        return root.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(reference[prefix.Length..]);
    }
}
