using System.Xml.Linq;

namespace Husaynia.ArchitectureTests;

public sealed class DependencyDirectionTests
{
    [Theory]
    [InlineData("src/Husaynia.Domain/Husaynia.Domain.csproj")]
    [InlineData("src/Husaynia.Application/Husaynia.Application.csproj", "src/Husaynia.Domain/Husaynia.Domain.csproj")]
    [InlineData(
        "src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj",
        "src/Husaynia.Application/Husaynia.Application.csproj",
        "src/Husaynia.Domain/Husaynia.Domain.csproj")]
    [InlineData(
        "src/Husaynia.Web/Husaynia.Web.csproj",
        "src/Husaynia.Application/Husaynia.Application.csproj",
        "src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj")]
    [InlineData(
        "tools/Husaynia.Migration/Husaynia.Migration.csproj",
        "src/Husaynia.Application/Husaynia.Application.csproj",
        "src/Husaynia.Infrastructure/Husaynia.Infrastructure.csproj")]
    public void ProductionProjectsHaveOnlyApprovedProjectReferences(
        string projectPath,
        params string[] expectedReferences)
    {
        var root = RepositoryRoot.Find();
        var project = XDocument.Load(Path.Combine(root, projectPath));
        var projectDirectory = Path.GetDirectoryName(Path.Combine(root, projectPath))!;

        var actualReferences = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(projectDirectory, include!))))
            .Select(Normalize)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var expected = expectedReferences
            .Select(Normalize)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actualReferences);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
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
