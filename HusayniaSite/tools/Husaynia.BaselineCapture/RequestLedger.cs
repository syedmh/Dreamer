namespace Husaynia.BaselineCapture;

public enum RequestTerminalKind
{
    Response,
    Failure,
    AttemptAborted,
    Capability
}

public sealed record RequestLedgerSnapshot(
    bool Passed,
    int DecisionCount,
    int TerminalCount,
    int AllowedInFlightCount,
    IReadOnlyList<string> ReasonCodes);

public sealed class RequestLedger
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RequestState> _requests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reasonCodes = new(StringComparer.Ordinal);
    private long _activityVersion;
    private bool _snapshotBarrierPassed;
    private bool _attemptAborting;
    private bool _abortFinalized;

    public void RegisterDecision(string requestId, ScreenshotNetworkDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(decision);
        lock (_sync)
        {
            Touch();
            if (!_requests.TryAdd(requestId, new RequestState(decision.Decision == "allow")))
            {
                _reasonCodes.Add("duplicate-request-decision");
                return;
            }

            if (decision.Decision is not ("allow" or "block")
                || string.IsNullOrWhiteSpace(decision.ReasonCode))
            {
                _reasonCodes.Add("unclassified-request-decision");
            }

            if (decision.Decision == "block")
            {
                var state = _requests[requestId];
                state.TerminalKind = RequestTerminalKind.Failure;
                state.TerminalReason = decision.ReasonCode;
            }
        }
    }

    public bool RecordResponse(string requestId, int statusCode)
    {
        lock (_sync)
        {
            if (!_requests.TryGetValue(requestId, out var state))
            {
                Touch();
                _reasonCodes.Add("untracked-response");
                return false;
            }

            if (!state.Allowed)
            {
                Touch();
                _reasonCodes.Add("blocked-request-continued");
                return false;
            }

            if (state.TerminalKind == RequestTerminalKind.AttemptAborted)
            {
                return false;
            }

            Touch();
            if (state.ResponseStatus is not null)
            {
                _reasonCodes.Add("duplicate-response");
                return false;
            }

            state.ResponseStatus = statusCode;
            return true;
        }
    }

    public bool RecordFinished(string requestId)
    {
        lock (_sync)
        {
            if (!_requests.TryGetValue(requestId, out var state))
            {
                Touch();
                _reasonCodes.Add("untracked-request-finished");
                return false;
            }

            if (!state.Allowed)
            {
                return false;
            }

            if (state.TerminalKind == RequestTerminalKind.AttemptAborted)
            {
                return false;
            }

            Touch();
            if (state.TerminalKind is not null)
            {
                _reasonCodes.Add("duplicate-request-terminal");
                return false;
            }

            if (state.ResponseStatus is null)
            {
                _reasonCodes.Add("response-status-missing");
                state.TerminalKind = RequestTerminalKind.Failure;
                state.TerminalReason = "response-status-missing";
                return true;
            }

            state.TerminalKind = RequestTerminalKind.Response;
            state.TerminalReason = $"http-{state.ResponseStatus.Value}";
            return true;
        }
    }

    public bool RecordFailure(string requestId, string reasonCode, string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        lock (_sync)
        {
            if (!_requests.TryGetValue(requestId, out var state))
            {
                Touch();
                _reasonCodes.Add("untracked-request-failure");
                return false;
            }

            if (!state.Allowed && state.TerminalKind == RequestTerminalKind.Failure)
            {
                return false;
            }

            if (state.TerminalKind == RequestTerminalKind.AttemptAborted)
            {
                return false;
            }

            Touch();
            if (state.TerminalKind is not null)
            {
                _reasonCodes.Add("duplicate-request-terminal");
                return false;
            }

            state.TerminalKind = RequestTerminalKind.Failure;
            state.TerminalReason = reasonCode;
            state.Failure = failure;
            return true;
        }
    }

    public bool RecordCapabilityTerminal(string requestId, string reasonCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        lock (_sync)
        {
            if (!_requests.TryGetValue(requestId, out var state))
            {
                Touch();
                _reasonCodes.Add("untracked-capability-terminal");
                return false;
            }

            if (!state.Allowed)
            {
                Touch();
                _reasonCodes.Add("blocked-capability-continued");
                return false;
            }

            Touch();
            if (state.TerminalKind is not null)
            {
                _reasonCodes.Add("duplicate-request-terminal");
                return false;
            }

            state.TerminalKind = RequestTerminalKind.Capability;
            state.TerminalReason = reasonCode;
            return true;
        }
    }

    public void MarkAttemptAborting()
    {
        lock (_sync)
        {
            if (_abortFinalized)
            {
                throw new InvalidOperationException(
                    "attempt-abort-already-finalized");
            }

            _attemptAborting = true;
        }
    }

    public IReadOnlyList<string> FinalizeAbortedAllowedRequests()
    {
        lock (_sync)
        {
            if (!_attemptAborting)
            {
                throw new InvalidOperationException(
                    "attempt-abort-not-started");
            }

            if (_abortFinalized)
            {
                return [];
            }

            var finalized = new List<string>();
            foreach (var pair in _requests.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                var state = pair.Value;
                if (!state.Allowed || state.TerminalKind is not null)
                {
                    continue;
                }

                state.ResponseStatus = null;
                state.TerminalKind = RequestTerminalKind.AttemptAborted;
                state.TerminalReason =
                    "attempt-aborted-before-browser-terminal";
                state.Failure = "capture-attempt-aborted";
                finalized.Add(pair.Key);
            }

            _abortFinalized = true;
            _snapshotBarrierPassed = true;
            return finalized;
        }
    }

    public async Task AwaitSnapshotBarrierAsync(
        TimeSpan quiescentWindow,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            quiescentWindow,
            TimeSpan.Zero);

        while (true)
        {
            long version;
            lock (_sync)
            {
                version = _activityVersion;
            }

            await Task.Delay(quiescentWindow, cancellationToken);
            lock (_sync)
            {
                if (version != _activityVersion || AllowedInFlightCountUnsafe() != 0)
                {
                    continue;
                }

                _snapshotBarrierPassed = true;
                return;
            }
        }
    }

    public RequestLedgerSnapshot Complete()
    {
        lock (_sync)
        {
            if (!_snapshotBarrierPassed)
            {
                _reasonCodes.Add("snapshot-barrier-missing");
            }

            var decisions = _requests.Count;
            var terminals = _requests.Values.Count(state => state.TerminalKind is not null);
            var inFlight = AllowedInFlightCountUnsafe();
            if (terminals != decisions)
            {
                _reasonCodes.Add("request-terminal-missing");
            }

            if (inFlight != 0)
            {
                _reasonCodes.Add("allowed-request-in-flight");
            }

            return new RequestLedgerSnapshot(
                _reasonCodes.Count == 0 && decisions == terminals && inFlight == 0,
                decisions,
                terminals,
                inFlight,
                _reasonCodes.Order(StringComparer.Ordinal).ToArray());
        }
    }

    private int AllowedInFlightCountUnsafe() =>
        _requests.Values.Count(state => state.Allowed && state.TerminalKind is null);

    private void Touch()
    {
        _activityVersion++;
        if (_snapshotBarrierPassed)
        {
            _reasonCodes.Add("post-barrier-activity");
        }
    }

    private sealed class RequestState(bool allowed)
    {
        public bool Allowed { get; } = allowed;
        public int? ResponseStatus { get; set; }
        public RequestTerminalKind? TerminalKind { get; set; }
        public string? TerminalReason { get; set; }
        public string? Failure { get; set; }
    }
}
