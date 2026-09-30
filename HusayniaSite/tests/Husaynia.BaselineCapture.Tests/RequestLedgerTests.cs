using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class RequestLedgerTests
{
    [Fact]
    public async Task AllowedResponseBecomesOneTerminalOnlyAfterFinished()
    {
        var ledger = new RequestLedger();
        ledger.RegisterDecision("request-1", Decision("request-1", "allow"));
        ledger.RecordResponse("request-1", 200);
        ledger.RecordFinished("request-1");
        await ledger.AwaitSnapshotBarrierAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        var snapshot = ledger.Complete();

        Assert.True(snapshot.Passed);
        Assert.Equal(1, snapshot.DecisionCount);
        Assert.Equal(1, snapshot.TerminalCount);
        Assert.Equal(0, snapshot.AllowedInFlightCount);
    }

    [Fact]
    public async Task AllowedFailureAndBlockedFailureHaveExactlyOneTerminal()
    {
        var ledger = new RequestLedger();
        ledger.RegisterDecision("allowed", Decision("allowed", "allow"));
        ledger.RecordFailure("allowed", "request-failed", "net::ERR_FAILED");
        ledger.RegisterDecision("blocked", Decision("blocked", "block"));
        ledger.RecordFailure("blocked", "request-failed", "blockedbyclient");
        await ledger.AwaitSnapshotBarrierAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);

        var snapshot = ledger.Complete();

        Assert.True(snapshot.Passed);
        Assert.Equal(2, snapshot.DecisionCount);
        Assert.Equal(2, snapshot.TerminalCount);
    }

    [Fact]
    public async Task MissingDuplicateUntrackedAndPostBarrierActivityFailClosed()
    {
        var ledger = new RequestLedger();
        ledger.RegisterDecision("missing", Decision("missing", "allow"));
        ledger.RegisterDecision("missing", Decision("missing", "allow"));
        ledger.RecordResponse("untracked", 200);
        ledger.RecordFailure("missing", "request-failed", null);
        ledger.RegisterDecision("blocked", Decision("blocked", "block"));
        ledger.RecordResponse("blocked", 200);
        await ledger.AwaitSnapshotBarrierAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None);
        ledger.RegisterDecision("late", Decision("late", "block"));

        var snapshot = ledger.Complete();

        Assert.False(snapshot.Passed);
        Assert.Contains("duplicate-request-decision", snapshot.ReasonCodes);
        Assert.Contains("untracked-response", snapshot.ReasonCodes);
        Assert.Contains("blocked-request-continued", snapshot.ReasonCodes);
        Assert.Contains("post-barrier-activity", snapshot.ReasonCodes);
    }

    [Fact]
    public void AbortedAttemptLetsBrowserTerminalWinAndIgnoresLateDuplicates()
    {
        var ledger = new RequestLedger();
        ledger.RegisterDecision("browser-terminal", Decision("browser-terminal", "allow"));
        ledger.RegisterDecision("synthetic-terminal", Decision("synthetic-terminal", "allow"));

        ledger.MarkAttemptAborting();
        Assert.True(ledger.RecordResponse("browser-terminal", 200));
        Assert.True(ledger.RecordFinished("browser-terminal"));

        var synthetic = ledger.FinalizeAbortedAllowedRequests();

        Assert.Equal(["synthetic-terminal"], synthetic);
        Assert.False(ledger.RecordResponse("synthetic-terminal", 204));
        Assert.False(ledger.RecordFinished("synthetic-terminal"));
        Assert.False(ledger.RecordFailure(
            "synthetic-terminal",
            "allowed-request-failed",
            "playwright-request-failed"));

        var snapshot = ledger.Complete();
        Assert.True(snapshot.Passed);
        Assert.Equal(2, snapshot.DecisionCount);
        Assert.Equal(2, snapshot.TerminalCount);
        Assert.Equal(0, snapshot.AllowedInFlightCount);
        Assert.Empty(snapshot.ReasonCodes);
    }

    [Fact]
    public async Task BrowserTerminalAndSyntheticAbortRaceProducesExactlyOneTerminal()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var ledger = new RequestLedger();
            ledger.RegisterDecision("request", Decision("request", "allow"));
            Assert.True(ledger.RecordResponse("request", 200));
            ledger.MarkAttemptAborting();
            using var start = new ManualResetEventSlim();
            var browserTerminal = Task.Run(() =>
            {
                start.Wait();
                return ledger.RecordFinished("request");
            });
            var syntheticTerminal = Task.Run(() =>
            {
                start.Wait();
                return ledger.FinalizeAbortedAllowedRequests();
            });

            start.Set();
            var browserWon = await browserTerminal;
            var synthetic = await syntheticTerminal;

            Assert.NotEqual(browserWon, synthetic.Contains("request", StringComparer.Ordinal));
            var snapshot = ledger.Complete();
            Assert.True(snapshot.Passed);
            Assert.Equal(1, snapshot.DecisionCount);
            Assert.Equal(1, snapshot.TerminalCount);
            Assert.Equal(0, snapshot.AllowedInFlightCount);
            Assert.Empty(snapshot.ReasonCodes);
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
}
