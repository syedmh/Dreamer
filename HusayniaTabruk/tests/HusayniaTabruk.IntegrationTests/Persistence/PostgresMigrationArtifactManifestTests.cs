using System.Security.Cryptography;

namespace HusayniaTabruk.IntegrationTests.Persistence;

[Trait("Category", "Persistence")]
public sealed class PostgresMigrationArtifactManifestTests
{
    private static readonly string[] ExpectedArtifacts =
    [
        "20260815075156_InitialPostgresSchema.cs",
        "20260815075156_InitialPostgresSchema.Designer.cs",
        "20260815102612_T8CorrectivePostgresHardening.cs",
        "20260815102612_T8CorrectivePostgresHardening.Designer.cs",
        "20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql",
        "20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql",
        "TabrukDbContextModelSnapshot.cs",
    ];

    private static readonly Dictionary<string, string> FrozenInitialHashes =
        new(StringComparer.Ordinal)
        {
            ["20260815075156_InitialPostgresSchema.cs"] =
                "ad9bc814e73f8ac6b76c43f07b09f6a82ac0479501c657112525e2d485db2829",
            ["20260815075156_InitialPostgresSchema.Designer.cs"] =
                "2fd38406cedb8ca86a1153304d4b37bfbe1729b9c298813084abb6380d126bc1",
        };

    [Fact]
    public void ManifestPinsExactlyTheExpectedMigrationArtifacts()
    {
        string repositoryRoot = FindRepositoryRoot();
        string migrationsDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "HusayniaTabruk.Infrastructure",
            "Migrations");
        string manifestPath = Path.Combine(migrationsDirectory, "T8MigrationArtifacts.sha256");

        Assert.True(File.Exists(manifestPath), $"Missing manifest: {manifestPath}");

        Dictionary<string, string> manifestEntries = File.ReadAllLines(manifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseEntry)
            .ToDictionary(entry => entry.FileName, entry => entry.Hash, StringComparer.Ordinal);

        Assert.Equal(7, manifestEntries.Count);
        Assert.Equal(
            ExpectedArtifacts.OrderBy(fileName => fileName, StringComparer.Ordinal),
            manifestEntries.Keys.OrderBy(fileName => fileName, StringComparer.Ordinal));
        Assert.DoesNotContain(
            "PostgresLeastPrivilegeCatalog.cs",
            manifestEntries.Keys);

        foreach ((string artifact, string frozenHash) in FrozenInitialHashes)
        {
            Assert.Equal(frozenHash, manifestEntries[artifact]);
        }

        foreach (string artifact in ExpectedArtifacts)
        {
            string artifactPath = Path.Combine(migrationsDirectory, artifact);
            Assert.True(File.Exists(artifactPath), $"Missing pinned artifact: {artifactPath}");

            string actualHash = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(artifactPath)))
                .ToLowerInvariant();
            Assert.Equal(actualHash, manifestEntries[artifact]);
        }
    }

    private static (string FileName, string Hash) ParseEntry(string line)
    {
        string[] parts = line.Split(": ", 2, StringSplitOptions.None);
        Assert.Equal(2, parts.Length);
        Assert.False(string.IsNullOrWhiteSpace(parts[0]));
        Assert.Matches("^[0-9a-f]{64}$", parts[1]);
        return (parts[0], parts[1]);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HusayniaTabruk.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate HusayniaTabruk.sln from the test output directory.");
    }
}
