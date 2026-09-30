namespace Husaynia.ContractTests;

public sealed class SchemaContractTests
{
    private const string ValidRouteManifest = """
        {
          "schemaVersion":"1.0.0",
          "captureId":"capture-1",
          "capturedAtUtc":"2026-08-15T12:00:00Z",
          "sourceBaseUrl":"https://www.husaynia.org/",
          "routes":[{
            "routeId":"home",
            "legacyPath":"/",
            "canonicalPath":"/",
            "expectedStatus":200,
            "templateKey":"home",
            "indexable":true,
            "sitemap":true,
            "assetKeys":[],
            "dynamicRegionKeys":[],
            "evidenceRefs":["http/home.json"]
          }]
        }
        """;

    private const string ValidMigrationManifest = """
        {
          "schemaVersion":"1.0.0",
          "source":"crawl",
          "sourceVersion":"capture-1",
          "capturedAtUtc":"2026-08-15T12:00:00Z",
          "rightsProfile":"husaynia-owned",
          "mode":"dry-run",
          "candidates":[{
            "sourceKey":"page:home",
            "sourceVersion":"1",
            "sourceChecksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "kind":"page",
            "targetKey":"content:home",
            "payloadRef":"payload/home.json",
            "mediaRefs":[],
            "dependencyKeys":[],
            "decision":"create"
          }]
        }
        """;

    private const string ValidImportReport = """
        {
          "schemaVersion":"1.0.0",
          "planId":"11111111-1111-1111-1111-111111111111",
          "mode":"dry-run",
          "generatedAtUtc":"2026-08-15T12:00:00Z",
          "counts":{"total":1,"create":1,"update":0,"skip":0,"conflict":0,"orphan":0,"reject":0},
          "decisions":[{"sourceKey":"page:home","sourceVersion":"1","targetKey":"content:home","decision":"create"}],
          "unresolvedDependencies":[],
          "checksums":{"manifest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
        }
        """;

    private const string ValidReleaseManifest = """
        {
          "schemaVersion":"1.0.0",
          "version":"1.0.0",
          "commitSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "builtAtUtc":"2026-08-15T12:00:00Z",
          "dotnetSdk":"10.0.400",
          "files":[{
            "path":"app/Husaynia.Web.zip",
            "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "length":1
          }],
          "databaseCompatibility":"initial",
          "requiredConfigurationKeys":[],
          "prohibitedLiveConfigurationInNonProduction":[]
        }
        """;

    [Theory]
    [MemberData(nameof(ValidInstances))]
    public void PositiveInstancesValidate(string schemaPath, string instance)
    {
        using var validator = CreateValidator(schemaPath);
        Assert.Empty(validator.Validate(instance));
    }

    [Theory]
    [MemberData(nameof(InvalidInstances))]
    public void NegativeInstancesAreRejected(string schemaPath, string instance)
    {
        using var validator = CreateValidator(schemaPath);
        Assert.NotEmpty(validator.Validate(instance));
    }

    public static TheoryData<string, string> ValidInstances => new()
    {
        { "contracts/routes/route-manifest.schema.json", ValidRouteManifest },
        { "contracts/migration/import-manifest.schema.json", ValidMigrationManifest },
        {
            "contracts/migration/import-manifest.schema.json",
            ValidMigrationManifest.Replace(
                "\"mode\":\"dry-run\",",
                "\"mode\":\"apply\",\"planId\":\"11111111-1111-1111-1111-111111111111\",",
                StringComparison.Ordinal)
        },
        { "contracts/migration/import-report.schema.json", ValidImportReport },
        { "contracts/pipeline/release-manifest.schema.json", ValidReleaseManifest },
    };

    public static TheoryData<string, string> InvalidInstances => new()
    {
        {
            "contracts/routes/route-manifest.schema.json",
            ValidRouteManifest.Replace("\"expectedStatus\":200,", "\"expectedStatus\":301,", StringComparison.Ordinal)
        },
        {
            "contracts/routes/route-manifest.schema.json",
            ValidRouteManifest.Replace("\"templateKey\":\"home\",", "\"templateKey\":\"home\",\"unexpected\":true,", StringComparison.Ordinal)
        },
        {
            "contracts/migration/import-manifest.schema.json",
            ValidMigrationManifest.Replace("\"mode\":\"dry-run\",", "\"mode\":\"apply\",", StringComparison.Ordinal)
        },
        {
            "contracts/migration/import-manifest.schema.json",
            ValidMigrationManifest.Replace(
                "\"mode\":\"dry-run\",",
                "\"mode\":\"dry-run\",\"planId\":\"11111111-1111-1111-1111-111111111111\",",
                StringComparison.Ordinal)
        },
        {
            "contracts/migration/import-report.schema.json",
            ValidImportReport.Replace("\"total\":1", "\"total\":-1", StringComparison.Ordinal)
        },
        {
            "contracts/pipeline/release-manifest.schema.json",
            ValidReleaseManifest.Replace(
                "\"commitSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",",
                "\"commitSha\":\"short\",",
                StringComparison.Ordinal)
        },
    };

    private static JsonSchemaInstanceValidator CreateValidator(string relativePath) =>
        new(Path.Combine(RepositoryRoot.Find(), relativePath));
}

internal static class RepositoryRoot
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HusayniaSite.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate HusayniaSite.sln.");
    }
}
