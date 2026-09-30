using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HusayniaTabruk.Domain.Common.Enums;

namespace HusayniaTabruk.Domain.Tests.Architecture;

public sealed class DomainDependencyTests
{
    [Fact]
    public void EvaluatedDomainProjectHasNoProjectOrPackageDependencies()
    {
        EvaluatedItems dependencies = ReadEvaluatedDependencies(
            FindProject("src", "HusayniaTabruk.Domain", "HusayniaTabruk.Domain.csproj"));

        Assert.Empty(dependencies.ProjectReference);
        Assert.Empty(dependencies.PackageReference);
        Assert.Equal(["Microsoft.NETCore.App"], dependencies.FrameworkReference);
    }

    [Fact]
    public void DomainAssemblyHasNoFrameworkHttpIdentityOrProviderDependencies()
    {
        string[] forbiddenAssemblyPrefixes =
        [
            "HusayniaTabruk.",
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions.Identity",
            "Npgsql",
            "System.Net.Http",
        ];

        Assembly domainAssembly = typeof(MembershipStatus).Assembly;
        string[] forbiddenReferences = domainAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null
                && forbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Cast<string>()
            .ToArray();

        string[] forbiddenNamespacePrefixes =
        [
            "Microsoft.AspNetCore",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Extensions.Identity",
            "Npgsql",
            "System.Net.Http",
        ];

        string[] forbiddenNamespaces = domainAssembly
            .GetTypes()
            .Select(type => type.Namespace)
            .Where(name => name is not null
                && forbiddenNamespacePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Cast<string>()
            .ToArray();

        Assert.Empty(forbiddenReferences);
        Assert.Empty(forbiddenNamespaces);
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
            ReadIncludes(items, "ProjectReference"),
            ReadIncludes(items, "PackageReference"),
            ReadIncludes(items, "FrameworkReference"));
    }

    private static string[] ReadIncludes(JsonElement items, string itemName) =>
        items.GetProperty(itemName)
            .EnumerateArray()
            .Select(item => item.GetProperty("Identity").GetString())
            .Where(identity => identity is not null)
            .Cast<string>()
            .ToArray();

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

    private sealed record EvaluatedItems(
        string[] ProjectReference,
        string[] PackageReference,
        string[] FrameworkReference);
}
