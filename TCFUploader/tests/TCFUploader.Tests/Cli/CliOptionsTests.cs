using TCFUploader.Cli;
using TCFUploader.Tests.TestDoubles;

namespace TCFUploader.Tests.Cli;

[TestClass]
public sealed class CliOptionsTests
{
    [TestMethod]
    public void AC01_InvalidStartupInputs_ArgumentMatrixRejectsUnsafeValues()
    {
        Assert.IsInstanceOfType<CliParseResult.Error>(CliOptionsParser.Parse([]));
        Assert.IsInstanceOfType<CliParseResult.Error>(CliOptionsParser.Parse(["--token", "secret"]));
        Assert.IsInstanceOfType<CliParseResult.Error>(CliOptionsParser.Parse(["--folder", "x", "--folder", "y"]));
    }

    [TestMethod]
    public void AC22_WindowsPathEquivalence_CanonicalizesFolder()
    {
        using var paths = new TestPaths();
        var run = (CliParseResult.Run)CliOptionsParser.Parse(["--folder", Path.Combine(paths.Watch, ".")]);
        Assert.AreEqual(Path.GetFullPath(paths.Watch), run.Options.WatchedRoot, true);
        Assert.IsFalse(run.Options.BrowserLogin);
    }

    [TestMethod]
    public void BrowserLogin_ParsesExplicitFlagInEitherPosition()
    {
        using var paths = new TestPaths();
        var trailing = (CliParseResult.Run)CliOptionsParser.Parse(
            ["--folder", paths.Watch, "--browser-login"]);
        var leading = (CliParseResult.Run)CliOptionsParser.Parse(
            ["--browser-login", "--folder", paths.Watch]);

        Assert.IsTrue(trailing.Options.BrowserLogin);
        Assert.IsTrue(leading.Options.BrowserLogin);
        Assert.IsInstanceOfType<CliParseResult.Error>(
            CliOptionsParser.Parse(["--folder", paths.Watch, "--browser-login", "--browser-login"]));
    }
}
