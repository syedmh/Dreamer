using System.Text.Json;
using HusayniaTabruk.Api.ContractTests.Conventions;

namespace HusayniaTabruk.Api.ContractTests.OpenApi;

public sealed class EligibleParticipantOpenApiContractTests
{
    [Fact]
    public async Task EligibleParticipantOperationAndCheckedInDocumentAreFrozen()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        string generated = await api.Client.GetStringAsync("/openapi/v1.json");
        using JsonDocument document = JsonDocument.Parse(generated);
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/members/eligible-participants")
            .GetProperty("get");

        Assert.Equal(
            "ListEligibleSignupParticipants",
            operation.GetProperty("operationId").GetString());
        Assert.Equal(
            [
                "#/components/parameters/cursor",
                "#/components/parameters/pageSize",
            ],
            operation.GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter => parameter.GetProperty("$ref").GetString()!)
                .ToArray());
        Assert.Equal(
            ["200", "400", "401", "503"],
            operation.GetProperty("responses")
                .EnumerateObject()
                .Select(response => response.Name)
                .ToArray());
        Assert.Equal(
            "#/components/schemas/CursorPageOfEligibleSignupParticipantResponse",
            operation.GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString());

        string snapshotPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "api",
            "openapi.json");
        Assert.Equal(
            File.ReadAllText(snapshotPath).ReplaceLineEndings("\n"),
            generated);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(
                   Path.Combine(directory.FullName, "HusayniaTabruk.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the HusayniaTabruk repository root.");
    }
}
