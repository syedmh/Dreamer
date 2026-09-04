using HusayniaSMS.WinForms.SafeDemo;

namespace HusayniaSMS.Tests.Presentation;

[TestClass]
public sealed class StartupOptionsTests
{
    [TestMethod]
    public void NoArgumentsMeansProduction()
    {
        var result = StartupOptionsParser.Parse([]);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(StartupMode.Production, result.Options!.Mode);
        Assert.IsNull(result.Options.Scenario);
    }

    [TestMethod]
    public void SafeDemoDefaultsToAllSuccess()
    {
        var result = StartupOptionsParser.Parse(["--safe-demo"]);
        Assert.AreEqual(SafeDemoScenario.AllSuccess, result.Options!.Scenario);
    }

    [TestMethod]
    public void EveryDocumentedScenarioParses()
    {
        foreach (var scenario in new[] { "all-success", "mixed", "auth-failure", "delayed" })
        {
            var result = StartupOptionsParser.Parse(["--safe-demo", $"--scenario={scenario}"]);
            Assert.IsTrue(result.Succeeded, scenario);
            Assert.AreEqual(StartupMode.SafeDemo, result.Options!.Mode);
        }
    }

    [TestMethod]
    public void UnknownDuplicateOrProductionScenarioArgumentsFailClosed()
    {
        var invalid = new[]
        {
            new[] { "--unknown" },
            new[] { "--safe-demo", "--safe-demo" },
            new[] { "--scenario=mixed" },
            new[] { "--safe-demo", "--scenario=" },
            new[] { "--safe-demo", "--scenario=unknown" },
            new[] { "--safe-demo", "--scenario=mixed", "--scenario=delayed" }
        };
        foreach (var args in invalid)
        {
            var result = StartupOptionsParser.Parse(args);
            Assert.IsFalse(result.Succeeded, string.Join(' ', args));
            Assert.IsNull(result.Options);
        }
    }
}
