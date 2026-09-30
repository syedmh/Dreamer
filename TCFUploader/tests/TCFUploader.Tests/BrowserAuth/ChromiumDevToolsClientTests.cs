using TCFUploader.BrowserAuth;

namespace TCFUploader.Tests.BrowserAuth;

[TestClass]
public sealed class ChromiumDevToolsClientTests
{
    [TestMethod]
    public void TokenExtraction_AcceptsPersistedAndCompatibilityShapesOnly()
    {
        Assert.AreEqual(
            "persisted-token",
            ChromiumDevToolsClient.ExtractTokenFromPersistedRecord(
                """{"state":{"settings":{"fotoshare_token":"persisted-token","username":"ignored"},"user":{"profile":{"id":123}}}}"""));
        Assert.AreEqual(
            "compatibility-token",
            ChromiumDevToolsClient.ExtractTokenFromPersistedRecord(
                """{"settings":{"fotoshare_token":"compatibility-token"},"user":{"profile":{"id":"user-456"}},"cookies":"ignored"}"""));
        Assert.IsNull(ChromiumDevToolsClient.ExtractTokenFromPersistedRecord(
            """{"state":{"settings":{"firebase_token":"wrong-secret"}}}"""));
        Assert.IsNull(ChromiumDevToolsClient.ExtractTokenFromPersistedRecord("{bad"));
    }

    [TestMethod]
    public void TargetFiltering_RequiresHttpsDashboardAndExpectedLoopbackPort()
    {
        const int port = 49152;
        Assert.IsTrue(ChromiumDevToolsClient.IsDashboardTarget(
            new Uri("https://dash.lumabooth.com/event/example/upload"),
            new Uri($"ws://127.0.0.1:{port}/devtools/page/1"),
            port));
        Assert.IsFalse(ChromiumDevToolsClient.IsDashboardTarget(
            new Uri("https://evil.example/"),
            new Uri($"ws://127.0.0.1:{port}/devtools/page/1"),
            port));
        Assert.IsFalse(ChromiumDevToolsClient.IsDashboardTarget(
            new Uri("http://dash.lumabooth.com/"),
            new Uri($"ws://127.0.0.1:{port}/devtools/page/1"),
            port));
        Assert.IsFalse(ChromiumDevToolsClient.IsDashboardTarget(
            new Uri("https://dash.lumabooth.com:444/"),
            new Uri($"ws://127.0.0.1:{port}/devtools/page/1"),
            port));
        Assert.IsFalse(ChromiumDevToolsClient.IsDashboardTarget(
            new Uri("https://dash.lumabooth.com/"),
            new Uri($"ws://192.0.2.1:{port}/devtools/page/1"),
            port));
    }

    [TestMethod]
    public void EvaluationResponse_ReturnsOnlyMatchingStringValue()
    {
        Assert.AreEqual(
            ("captured-token", "user-123"),
            ChromiumDevToolsClient.ParseEvaluationResponse(
                """{"id":1,"result":{"result":{"type":"string","value":"{\"token\":\"captured-token\",\"userId\":\"user-123\"}"}}}""",
                1));
        Assert.IsNull(ChromiumDevToolsClient.ParseEvaluationResponse(
            """{"id":2,"result":{"result":{"type":"string","value":"wrong"}}}""",
            1));
        Assert.IsNull(ChromiumDevToolsClient.ParseEvaluationResponse(
            """{"id":1,"result":{"exceptionDetails":{"text":"secret-shaped-error"}}}""",
            1));
    }
}
