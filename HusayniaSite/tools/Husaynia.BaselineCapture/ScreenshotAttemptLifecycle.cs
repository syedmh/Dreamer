namespace Husaynia.BaselineCapture;

public sealed class ScreenshotAttemptLifecycle
{
    internal ScreenshotAttemptLifecycle(
        int attempt,
        object context,
        object page,
        RequestLedger ledger)
    {
        Attempt = attempt;
        Context = context;
        Page = page;
        Ledger = ledger;
    }

    public int Attempt { get; }
    public RequestLedger Ledger { get; }
    internal object Context { get; }
    internal object Page { get; }

    internal ScreenshotAttemptCompletion CompleteSnapshot(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        var snapshot = Ledger.Complete();
        return new(snapshot, snapshot.Passed ? png : null);
    }
}

internal sealed record ScreenshotAttemptCompletion(
    RequestLedgerSnapshot Ledger,
    byte[]? RetainedPng);

public sealed class ScreenshotAttemptLifecycleFactory
{
    private readonly HashSet<object> _contexts = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<RequestLedger> _ledgers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _attempts = [];

    public ScreenshotAttemptLifecycle Create(
        int attempt,
        object context,
        object page,
        RequestLedger ledger)
    {
        if (attempt <= 0 || !_attempts.Add(attempt))
        {
            throw new InvalidOperationException("attempt-number-reused");
        }

        if (!_contexts.Add(context))
        {
            throw new InvalidOperationException("attempt-context-reused");
        }

        if (!_pages.Add(page))
        {
            throw new InvalidOperationException("attempt-page-reused");
        }

        if (!_ledgers.Add(ledger))
        {
            throw new InvalidOperationException("attempt-ledger-reused");
        }

        return new ScreenshotAttemptLifecycle(attempt, context, page, ledger);
    }
}
