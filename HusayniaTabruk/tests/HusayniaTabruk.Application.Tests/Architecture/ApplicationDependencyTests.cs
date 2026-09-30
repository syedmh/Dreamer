using System.Diagnostics;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Time;
using Xunit.Sdk;

namespace HusayniaTabruk.Application.Tests.Architecture;

public sealed class ApplicationDependencyTests
{
    [Fact]
    public void EvaluatedApplicationProjectReferencesOnlyDomainAndHasNoPackagesOrExtraFrameworks()
    {
        AssertApplicationDependencies(
            FindProject("src", "HusayniaTabruk.Application", "HusayniaTabruk.Application.csproj"));
    }

    [Fact]
    public void ApplicationAssemblyReferencesOnlyAllowedRuntimeDependencies()
    {
        string[] forbiddenReferences = typeof(IClock).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && IsForbiddenRuntimeDependency(name))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(forbiddenReferences);
    }

    [Fact]
    public void ProductionProjectReferencesFollowTheFrozenDependencyDirection()
    {
        AssertProjectReferences("HusayniaTabruk.Domain", []);
        AssertProjectReferences("HusayniaTabruk.Application", ["HusayniaTabruk.Domain.csproj"]);
        AssertProjectReferences("HusayniaTabruk.Infrastructure", ["HusayniaTabruk.Application.csproj"]);
        AssertProjectReferences(
            "HusayniaTabruk.Api",
            ["HusayniaTabruk.Application.csproj", "HusayniaTabruk.Infrastructure.csproj"]);
    }

    [Fact]
    public void DependencyGuardDetectsImportedForbiddenProjectReference()
    {
        using TemporaryApplicationProject project = TemporaryApplicationProject.Create(
            importedPropsContent:
            """
            <Project>
              <ItemGroup>
                <ProjectReference Include="HusayniaTabruk.Infrastructure.csproj" />
              </ItemGroup>
            </Project>
            """,
            additionalProjects:
            [
                "HusayniaTabruk.Infrastructure.csproj",
            ]);

        XunitException exception = Assert.Throws<XunitException>(() => AssertApplicationDependencies(project.ProjectPath));

        Assert.Contains("ProjectReference", exception.Message);
        Assert.Contains("HusayniaTabruk.Infrastructure.csproj", exception.Message);
        Assert.Contains(project.ImportedPropsPath, exception.Message);
    }

    [Fact]
    public void DependencyGuardDetectsImportedForbiddenPackageReference()
    {
        using TemporaryApplicationProject project = TemporaryApplicationProject.Create(
            importedTargetsContent:
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="Npgsql" Version="9.0.3" />
              </ItemGroup>
            </Project>
            """);

        XunitException exception = Assert.Throws<XunitException>(() => AssertApplicationDependencies(project.ProjectPath));

        Assert.Contains("PackageReference", exception.Message);
        Assert.Contains("Npgsql", exception.Message);
        Assert.Contains(project.ImportedTargetsPath, exception.Message);
    }

    [Fact]
    public void DependencyGuardDetectsImportedForbiddenFrameworkReference()
    {
        using TemporaryApplicationProject project = TemporaryApplicationProject.Create(
            importedTargetsContent:
            """
            <Project>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>
            </Project>
            """);

        XunitException exception = Assert.Throws<XunitException>(() => AssertApplicationDependencies(project.ProjectPath));

        Assert.Contains("FrameworkReference", exception.Message);
        Assert.Contains("Microsoft.AspNetCore.App", exception.Message);
        Assert.Contains(project.ImportedTargetsPath, exception.Message);
    }

    private static bool IsForbiddenRuntimeDependency(string assemblyName) =>
        assemblyName is "HusayniaTabruk.Infrastructure" or "HusayniaTabruk.Api"
        || assemblyName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.Extensions.Identity", StringComparison.Ordinal)
        || assemblyName.StartsWith("Npgsql", StringComparison.Ordinal)
        || assemblyName.StartsWith("System.Net.Http", StringComparison.Ordinal);

    private static void AssertApplicationDependencies(string projectPath)
    {
        EvaluatedItems dependencies = ReadEvaluatedDependencies(projectPath);

        AssertExactItemSet(
            Path.GetFileName(projectPath),
            "ProjectReference",
            dependencies.ProjectReference,
            item => Path.GetFileName(item.Identity),
            ["HusayniaTabruk.Domain.csproj"]);
        AssertExactItemSet(
            Path.GetFileName(projectPath),
            "PackageReference",
            dependencies.PackageReference,
            item => item.Identity,
            []);
        AssertExactItemSet(
            Path.GetFileName(projectPath),
            "FrameworkReference",
            dependencies.FrameworkReference,
            item => item.Identity,
            ["Microsoft.NETCore.App"]);
    }

    private static void AssertProjectReferences(string projectName, string[] expectedReferences)
    {
        EvaluatedItems dependencies = ReadEvaluatedDependencies(
            FindProject("src", projectName, $"{projectName}.csproj"));

        AssertExactItemSet(
            projectName,
            "ProjectReference",
            dependencies.ProjectReference,
            item => Path.GetFileName(item.Identity),
            expectedReferences);
    }

    private static void AssertExactItemSet(
        string projectName,
        string itemName,
        EvaluatedItem[] items,
        Func<EvaluatedItem, string> normalizeIdentity,
        string[] expectedIdentities)
    {
        string[] actual = items
            .Select(normalizeIdentity)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = expectedIdentities
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (!actual.SequenceEqual(expected))
        {
            throw new XunitException(
                $"{projectName} resolved unexpected {itemName} items.{Environment.NewLine}" +
                $"Expected: [{string.Join(", ", expected)}]{Environment.NewLine}" +
                $"Actual:{Environment.NewLine}{FormatItems(items, normalizeIdentity)}");
        }
    }

    private static string FormatItems(
        IEnumerable<EvaluatedItem> items,
        Func<EvaluatedItem, string> normalizeIdentity)
    {
        EvaluatedItem[] materialized = items.ToArray();
        if (materialized.Length == 0)
        {
            return "<none>";
        }

        return string.Join(
            Environment.NewLine,
            materialized.Select(item =>
                $"- {normalizeIdentity(item)} (Identity={item.Identity}; DefiningProjectFullPath={item.DefiningProjectFullPath ?? "<unknown>"})"));
    }

    private static EvaluatedItems ReadEvaluatedDependencies(string projectPath)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{projectPath}\" -getItem:ProjectReference -getItem:PackageReference -getItem:FrameworkReference",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"MSBuild evaluation failed.{Environment.NewLine}{error}");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement items = document.RootElement.GetProperty("Items");

        return new(
            ReadItems(items, "ProjectReference"),
            ReadItems(items, "PackageReference"),
            ReadItems(items, "FrameworkReference"));
    }

    private static EvaluatedItem[] ReadItems(JsonElement items, string itemName) =>
        items.TryGetProperty(itemName, out JsonElement itemElements)
            ? itemElements.EnumerateArray()
                .Select(item => new EvaluatedItem(
                    item.GetProperty("Identity").GetString() ?? string.Empty,
                    item.TryGetProperty("DefiningProjectFullPath", out JsonElement definingProject)
                        ? definingProject.GetString()
                        : null))
                .ToArray()
            : [];

    private static string FindProject(params string[] relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record EvaluatedItem(string Identity, string? DefiningProjectFullPath);

    private sealed record EvaluatedItems(
        EvaluatedItem[] ProjectReference,
        EvaluatedItem[] PackageReference,
        EvaluatedItem[] FrameworkReference);

    private sealed class TemporaryApplicationProject : IDisposable
    {
        private const string MinimalProject =
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        private TemporaryApplicationProject(string rootPath)
        {
            RootPath = rootPath;
            ProjectPath = Path.Combine(rootPath, "ApplicationDependencyMutation.csproj");
            ImportedPropsPath = Path.Combine(rootPath, "Imported.Dependency.props");
            ImportedTargetsPath = Path.Combine(rootPath, "Imported.Dependency.targets");
        }

        public string RootPath { get; }
        public string ProjectPath { get; }
        public string ImportedPropsPath { get; }
        public string ImportedTargetsPath { get; }

        public static TemporaryApplicationProject Create(
            string? importedPropsContent = null,
            string? importedTargetsContent = null,
            params string[] additionalProjects)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "HusayniaTabruk.ApplicationDependencyTests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(rootPath);

            TemporaryApplicationProject project = new(rootPath);
            File.WriteAllText(
                project.ProjectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="Imported.Dependency.props" Condition="Exists('Imported.Dependency.props')" />
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="HusayniaTabruk.Domain.csproj" />
                  </ItemGroup>
                  <Import Project="Imported.Dependency.targets" Condition="Exists('Imported.Dependency.targets')" />
                </Project>
                """);
            File.WriteAllText(Path.Combine(rootPath, "HusayniaTabruk.Domain.csproj"), MinimalProject);

            foreach (string projectName in additionalProjects)
            {
                File.WriteAllText(Path.Combine(rootPath, projectName), MinimalProject);
            }

            if (importedPropsContent is not null)
            {
                File.WriteAllText(project.ImportedPropsPath, importedPropsContent);
            }

            if (importedTargetsContent is not null)
            {
                File.WriteAllText(project.ImportedTargetsPath, importedTargetsContent);
            }

            return project;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
