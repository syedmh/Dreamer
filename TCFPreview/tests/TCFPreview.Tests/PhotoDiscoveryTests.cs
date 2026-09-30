using TCFPreview.Core;

namespace TCFPreview.Tests;

[TestClass]
public sealed class PhotoDiscoveryTests
{
    [TestMethod]
    public void Discover_IncludesEverySupportedExtensionCaseInsensitively()
    {
        using TempDirectory directory = new();
        string[] supportedNames =
        [
            "01.JpG",
            "02.jPeG",
            "03.PnG",
            "04.BmP",
            "05.GiF",
            "06.TiF",
            "07.TiFf",
        ];

        foreach (string name in supportedNames)
        {
            directory.CreateFile(name);
        }

        IReadOnlyList<string> result = PhotoDiscovery.Discover(directory.Path);

        CollectionAssert.AreEqual(
            supportedNames.Select(name => Path.Combine(directory.Path, name)).ToArray(),
            result.ToArray());
    }

    [TestMethod]
    public void Discover_FiltersNonRecursivelyAndOrdersByFileName()
    {
        using TempDirectory directory = new();
        directory.CreateFile("zeta.JPG");
        directory.CreateFile("Alpha.png");
        directory.CreateFile("middle.TiFf");
        directory.CreateFile("notes.txt");
        directory.CreateFile(Path.Combine("nested", "hidden.jpeg"));

        IReadOnlyList<string> result = PhotoDiscovery.Discover(directory.Path);

        CollectionAssert.AreEqual(
            new[]
            {
                Path.Combine(directory.Path, "Alpha.png"),
                Path.Combine(directory.Path, "middle.TiFf"),
                Path.Combine(directory.Path, "zeta.JPG"),
            },
            result.ToArray());
    }

    [TestMethod]
    public void Discover_EmptyDirectoryReturnsEmptyCollection()
    {
        using TempDirectory directory = new();

        IReadOnlyList<string> result = PhotoDiscovery.Discover(directory.Path);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void Discover_MissingDirectoryThrowsExplicitError()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        DirectoryNotFoundException exception = Assert.ThrowsExactly<DirectoryNotFoundException>(
            () => PhotoDiscovery.Discover(missingPath));

        StringAssert.Contains(exception.Message, missingPath);
    }
}
