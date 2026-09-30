using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.ContractTests.Conventions;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Cancellation;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using Microsoft.AspNetCore.Http;

namespace HusayniaTabruk.Api.ContractTests.Signups.Cancellation;

public sealed class SignupCancellationEndpointContractTests
{
    [Fact]
    public async Task OverridePublishesOnlyTheSupportedCancelledTargetState()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));

        JsonElement schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("OverrideSignupRequest");
        string[] targetStates = schema
            .GetProperty("properties")
            .GetProperty("targetState")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();

        Assert.Equal(["cancelled"], targetStates);
        Assert.Equal(
            ["targetState", "reason"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task OverrideParserRejectsNonContractTargetStateCasing()
    {
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes(
                """{"targetState":"CANCELLED","reason":"Operational reason"}"""));

        Result<ParsedCancellationOverride> result =
            await SignupCancellationEndpointSupport.ParseOverrideAsync(
                context.Request,
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            SignupApplicationErrorCodes.InvalidSignupRequest,
            result.Error.Code);
    }
}
