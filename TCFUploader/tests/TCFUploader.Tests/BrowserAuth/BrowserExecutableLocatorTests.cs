using TCFUploader.BrowserAuth;
using TCFUploader.Tests.TestDoubles;

namespace TCFUploader.Tests.BrowserAuth;

[TestClass]
public sealed class BrowserExecutableLocatorTests
{
    [TestMethod]
    public void ExactOverride_RequiresExistingFile()
    {
        using var paths = new TestPaths();
        var executable = Path.Combine(paths.Root, "custom-browser.exe");
        File.WriteAllText(executable, string.Empty);

        var found = new BrowserExecutableLocator(_ => executable).Locate();
        Assert.AreEqual(
            Path.GetFullPath(executable),
            ((BrowserExecutableResult.Success)found).Executable.Path);

        var missing = new BrowserExecutableLocator(
            _ => Path.Combine(paths.Root, "missing.exe")).Locate();
        Assert.AreEqual("browser_path_invalid", ((BrowserExecutableResult.Error)missing).Code);
    }
}
