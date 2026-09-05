using HusayniaSMS.Core.Contacts;
using HusayniaSMS.WinForms.Infrastructure.Csv;
using HusayniaSMS.WinForms.SafeDemo;

namespace HusayniaSMS.Tests.Documentation;

[TestClass]
public sealed class SampleCsvTests
{
    [TestMethod]
    public async Task SampleIsQuotedFictitiousImportableAndHasNoLiveSendHook()
    {
        var root = FindSolutionRoot();
        var path = Path.Combine(root, "samples", "contacts.sample.csv");
        var text = await File.ReadAllTextAsync(path);
        StringAssert.Contains(text, "\"Husaynia Office, Example\"");
        StringAssert.Contains(text, "\"Ali \"\"Sample\"\" Person\"");
        Assert.IsTrue(text.Split('\n').Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .All(line => line.Contains("+1202555010", StringComparison.Ordinal)));

        var phone = new E164PhoneNumberValidator();
        var store = new CsvHelperContactCsvStore(new ContactRowValidator(phone));
        var result = await store.LoadAsync(path, default);
        Assert.AreEqual(CsvImportStatus.Success, result.Status);
        Assert.AreEqual(3, result.Rows.Count);
        Assert.IsTrue(result.Rows.All(row => row.IsEligible));
        Assert.IsFalse(text.Contains("Twilio", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(typeof(ScriptedFakeTwilioTransportFactory),
            typeof(ScriptedFakeTwilioTransportFactory));
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "HusayniaSMS.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        Assert.Fail("Could not locate solution root.");
        return string.Empty;
    }
}
