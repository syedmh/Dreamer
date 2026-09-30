using Husaynia.BaselineCapture;
using Microsoft.Playwright;
using System.Text.Json;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class ScreenshotAttemptLifecycleTests
{
    [Fact]
    public async Task AttemptFactoryEnforcesFreshContextPageLedgerAndRejectsLateActivity()
    {
        var factory = new ScreenshotAttemptLifecycleFactory();
        var context1 = new object();
        var page1 = new object();
        var ledger1 = new RequestLedger();
        var first = factory.Create(1, context1, page1, ledger1);
        var second = factory.Create(2, new object(), new object(), new RequestLedger());

        Assert.Equal(3, PlaywrightScreenshotCapture.MaximumNavigationAttempts);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Ledger, second.Ledger);
        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(3, context1, new object(), new RequestLedger()));
        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(4, new object(), page1, new RequestLedger()));
        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(5, new object(), new object(), ledger1));

        ledger1.RegisterDecision("request", Decision("request", "allow"));
        ledger1.RecordResponse("request", 200);
        ledger1.RecordFinished("request");
        await ledger1.AwaitSnapshotBarrierAsync(
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);
        ledger1.RegisterDecision("late", Decision("late", "block"));

        var completion = first.CompleteSnapshot([1, 2, 3]);
        var snapshot = completion.Ledger;

        Assert.False(snapshot.Passed);
        Assert.Contains("post-barrier-activity", snapshot.ReasonCodes);
        Assert.Null(completion.RetainedPng);
    }

    [Fact]
    public async Task AttemptResourceLifetimeDisposesPageAndContextExactlyOnce()
    {
        var context = new TrackingAsyncDisposable();
        var page = new TrackingAsyncDisposable();

        await using (var contextLifetime = new AttemptResourceLifetime(context.DisposeAsync))
        await using (var pageLifetime = new AttemptResourceLifetime(page.DisposeAsync))
        {
            Assert.Equal(0, context.DisposeCount);
            Assert.Equal(0, page.DisposeCount);
        }

        Assert.Equal(1, context.DisposeCount);
        Assert.Equal(1, page.DisposeCount);
    }

    [Fact]
    public void CookiePolicyRejectsCreatedAndRequestCookies()
    {
        Assert.Null(BrowserCookiePolicy.ValidateContextCookieCount(0));
        Assert.Equal("cookies-created", BrowserCookiePolicy.ValidateContextCookieCount(1));
        Assert.Null(BrowserCookiePolicy.ValidateRequestHeaders(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["accept"] = "text/html"
            }));
        Assert.Equal(
            "cookie-request-blocked",
            BrowserCookiePolicy.ValidateRequestHeaders(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cookie"] = "session=secret"
                }));
        Assert.Equal(
            new ScreenshotPolicyDecision(
                false,
                "capability-cookie-write-blocked"),
            BrowserCookiePolicy.ClassifyCapability("cookie-write"));
        Assert.Equal(
            new ScreenshotPolicyDecision(
                true,
                "cookie-read-empty-context"),
            BrowserCookiePolicy.ClassifyCapability("cookie-read"));
        Assert.Equal(
            new ScreenshotPolicyDecision(
                false,
                "capability-websocket-blocked"),
            BrowserCookiePolicy.ClassifyCapability("websocket"));
    }

    [Fact]
    public async Task EmptyCookieReadHasExactlyOneAllowedDecisionAndCapabilityTerminal()
    {
        var ledger = new RequestLedger();
        var decision = new ScreenshotNetworkDecision(
            "home|desktop",
            "cookie-read-1",
            DateTimeOffset.UtcNow,
            "capability",
            "COOKIE-READ",
            "https://www.husaynia.org/",
            "capability",
            false,
            "allow",
            "cookie-read-empty-context",
            null,
            null,
            CaptureProfile.Approved.PolicyVersion,
            1,
            false,
            null);

        ledger.RegisterDecision(decision.RequestId, decision);
        Assert.True(ledger.RecordCapabilityTerminal(
            decision.RequestId,
            "cookie-read-empty-context"));
        await ledger.AwaitSnapshotBarrierAsync(
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);

        var snapshot = ledger.Complete();
        Assert.True(snapshot.Passed);
        Assert.Equal(1, snapshot.DecisionCount);
        Assert.Equal(1, snapshot.TerminalCount);
        Assert.Equal(0, snapshot.AllowedInFlightCount);
    }

    [Fact]
    public async Task ProductionCapabilityInjectionAllowsOnlyEmptyCookieReads()
    {
        const string nonce = "cookie-production-proof";
        await using var browser = await VerifiedBrowserSession.CreateAsync(
            CaptureProfile.Approved,
            ["MAP * ~NOTFOUND"],
            CancellationToken.None);
        await using var context = await browser.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var messages = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.StartsWith(
                    CapabilityConsolePolicy.Prefix,
                    StringComparison.Ordinal))
            {
                messages.Add(message.Text);
            }
        };
        await page.AddInitScriptAsync(
            """
            Object.defineProperty(window, 'cookieStore', {
              configurable: true,
              value: {
                get() { return Promise.resolve('inherited'); },
                getAll() { return Promise.resolve(['inherited']); },
                set() { return Promise.resolve(); },
                delete() { return Promise.resolve(); }
              }
            });
            """);
        await page.AddInitScriptAsync(
            PlaywrightScreenshotCapture.CreateCapabilityBlockScript(nonce));
        await page.GotoAsync("data:text/html,<html><body>cookie-proof</body></html>");

        var result = await page.EvaluateAsync<JsonElement>(
            """
            async () => {
              const blocked = async action => {
                try {
                  await action();
                  return false;
                } catch (error) {
                  return error && error.name === 'SecurityError';
                }
              };
              return {
                documentCookie: document.cookie,
                cookieStoreGet: await cookieStore.get(),
                cookieStoreGetAll: await cookieStore.getAll(),
                documentSetBlocked: await blocked(() => { document.cookie = 'x=y'; }),
                storeSetBlocked: await blocked(() => cookieStore.set('x', 'y')),
                storeDeleteBlocked: await blocked(() => cookieStore.delete('x')),
                policyFailures: window.__HUSAYNIA_BASELINE_CAPABILITY_POLICY_FAILURES__
              };
            }
            """);

        Assert.Equal(string.Empty, result.GetProperty("documentCookie").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("cookieStoreGet").ValueKind);
        Assert.Empty(result.GetProperty("cookieStoreGetAll").EnumerateArray());
        Assert.True(result.GetProperty("documentSetBlocked").GetBoolean());
        Assert.True(result.GetProperty("storeSetBlocked").GetBoolean());
        Assert.True(result.GetProperty("storeDeleteBlocked").GetBoolean());
        Assert.Empty(result.GetProperty("policyFailures").EnumerateArray());

        var parsed = messages
            .Select((message, index) => CapabilityConsolePolicy.Parse(
                message,
                index + 1,
                page.Url,
                nonce))
            .ToArray();
        Assert.All(parsed, item => Assert.True(item.Accepted));
        Assert.Equal(3, parsed.Count(item => item.Capability == "cookie-read"));
        Assert.Equal(3, parsed.Count(item => item.Capability == "cookie-write"));
        Assert.All(
            parsed.Where(item => item.Capability == "cookie-read"),
            item => Assert.Equal(
                new ScreenshotPolicyDecision(true, "cookie-read-empty-context"),
                BrowserCookiePolicy.ClassifyCapability(item.Capability)));
        Assert.All(
            parsed.Where(item => item.Capability == "cookie-write"),
            item => Assert.Equal(
                new ScreenshotPolicyDecision(false, "capability-cookie-write-blocked"),
                BrowserCookiePolicy.ClassifyCapability(item.Capability)));

        var ledger = new RequestLedger();
        foreach (var (item, index) in parsed
                     .Where(item => item.Capability == "cookie-read")
                     .Select((item, index) => (item, index)))
        {
            var requestId = $"cookie-read-{index + 1}";
            ledger.RegisterDecision(
                requestId,
                new ScreenshotNetworkDecision(
                    "home|desktop",
                    requestId,
                    DateTimeOffset.UtcNow,
                    "capability",
                    "COOKIE-READ",
                    page.Url,
                    "capability",
                    false,
                    "allow",
                    "cookie-read-empty-context",
                    null,
                    null,
                    CaptureProfile.Approved.PolicyVersion,
                    1,
                    false,
                    null));
            Assert.True(ledger.RecordCapabilityTerminal(
                requestId,
                "cookie-read-empty-context"));
        }

        await ledger.AwaitSnapshotBarrierAsync(
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        var snapshot = ledger.Complete();
        Assert.Equal(3, snapshot.DecisionCount);
        Assert.Equal(3, snapshot.TerminalCount);
        Assert.True(snapshot.Passed);
    }

    [Fact]
    public void CapabilityConsolePolicyIsAllowlistedAndBounded()
    {
        const string nonce = "test-capability-nonce";
        var valid = CapabilityConsolePolicy.Parse(
            $"{CapabilityConsolePolicy.Prefix}{{\"capability\":\"websocket\",\"url\":\"https://www.husaynia.org/\",\"nonce\":\"{nonce}\"}}",
            1,
            "https://www.husaynia.org/",
            nonce);
        var malformed = CapabilityConsolePolicy.Parse(
            $"{CapabilityConsolePolicy.Prefix}{{",
            2,
            "https://www.husaynia.org/",
            nonce);
        var unapproved = CapabilityConsolePolicy.Parse(
            $"{CapabilityConsolePolicy.Prefix}{{\"capability\":\"arbitrary\",\"url\":\"https://www.husaynia.org/\",\"nonce\":\"{nonce}\"}}",
            3,
            "https://www.husaynia.org/",
            nonce);
        var spoofed = CapabilityConsolePolicy.Parse(
            $"{CapabilityConsolePolicy.Prefix}{{\"capability\":\"websocket\",\"url\":\"https://www.husaynia.org/\",\"nonce\":\"attacker\"}}",
            4,
            "https://www.husaynia.org/",
            nonce);
        var oversize = CapabilityConsolePolicy.Parse(
            $"{CapabilityConsolePolicy.Prefix}{new string('x', CapabilityConsolePolicy.MaximumMessageCharacters)}",
            5,
            "https://www.husaynia.org/",
            nonce);
        var excess = CapabilityConsolePolicy.Parse(
            $"{CapabilityConsolePolicy.Prefix}{{\"capability\":\"websocket\",\"url\":\"https://www.husaynia.org/\",\"nonce\":\"{nonce}\"}}",
            CapabilityConsolePolicy.MaximumMessageCount + 1,
            "https://www.husaynia.org/",
            nonce);

        Assert.True(valid.Accepted);
        Assert.Equal("websocket", valid.Capability);
        Assert.Equal("malformed-capability-log", malformed.FailureReason);
        Assert.Equal("malformed-capability-log", unapproved.FailureReason);
        Assert.Equal("malformed-capability-log", spoofed.FailureReason);
        Assert.Equal("capability-log-oversize", oversize.FailureReason);
        Assert.Equal("capability-log-count-exceeded", excess.FailureReason);
    }

    [Fact]
    public void DonationCaptureContainsNoInteractionOrSubmissionCalls()
    {
        var source = File.ReadAllText(Path.Combine(
            BaselineTestFixture.FindRepositoryRoot(),
            "tools",
            "Husaynia.BaselineCapture",
            "PlaywrightScreenshotCapture.cs"));

        Assert.DoesNotContain(".ClickAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".FillAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".TypeAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".PressAsync(", source, StringComparison.Ordinal);
        Assert.Contains(
            "install(HTMLFormElement.prototype, 'submit'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "install(HTMLFormElement.prototype, 'requestSubmit'",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NonEmptyRunOutputIsRefusedWithoutDeletion()
    {
        var output = Path.Combine(Path.GetTempPath(), $"husaynia-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        var retained = Path.Combine(output, "retain.txt");
        File.WriteAllText(retained, "retain");
        try
        {
            var exception = Assert.Throws<CaptureSafetyException>(() =>
                CaptureIO.EnsureEmptyOutputDirectory(output));

            Assert.Equal("output-directory-not-empty", exception.ReasonCode);
            Assert.Equal("retain", File.ReadAllText(retained));
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static ScreenshotNetworkDecision Decision(string requestId, string decision) =>
        new(
            "home|mobile-320x568",
            requestId,
            DateTimeOffset.UtcNow,
            "request",
            "GET",
            "https://www.husaynia.org/",
            "document",
            true,
            decision,
            decision == "allow" ? "primary-origin-get-head" : "policy-blocked",
            null,
            null,
            CaptureProfile.Approved.PolicyVersion,
            1,
            true,
            "93.184.216.34");

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
