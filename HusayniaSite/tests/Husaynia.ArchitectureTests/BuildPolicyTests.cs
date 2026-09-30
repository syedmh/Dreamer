using System.Text.Json;
using System.Xml.Linq;

namespace Husaynia.ArchitectureTests;

public sealed class BuildPolicyTests
{
    [Fact]
    public void GlobalJsonPinsExactApprovedSdkFeatureBand()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "global.json")));
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Equal("10.0.400", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    [Fact]
    public void BuildPolicyDoesNotExemptNuGetVulnerabilityWarnings()
    {
        var properties = XDocument.Load(Path.Combine(RepositoryRoot.Find(), "Directory.Build.props"));
        var warningsNotAsErrors = properties
            .Descendants("WarningsNotAsErrors")
            .Select(element => element.Value);

        Assert.DoesNotContain(warningsNotAsErrors, value => value.Contains("NU1900", StringComparison.Ordinal));
    }
}
