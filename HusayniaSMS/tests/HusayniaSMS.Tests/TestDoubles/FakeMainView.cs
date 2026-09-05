using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeMainView : IMainView
{
    private readonly ManualResetEventSlim _progressRelease = new(initialState: false);
    private int _progressInFlight;
    private int _maximumConcurrentProgress;

    public string MessageText { get; set; } = string.Empty;
    public IReadOnlyList<int> CheckedRecipientOrdinals { get; set; } = Array.Empty<int>();
    public IReadOnlyList<int> HighlightedContactOrdinals { get; set; } = Array.Empty<int>();
    public SetupInput SetupInput { get; set; } = new(
        "",
        TwilioSenderMode.FromPhoneNumber,
        "",
        "",
        false);
    public SettingsDescriptor? Descriptor { get; private set; }
    public string? Placeholder { get; private set; }
    public IReadOnlyList<ContactGridRowViewModel> Rows { get; private set; } =
        Array.Empty<ContactGridRowViewModel>();
    public IReadOnlySet<int> AppliedSelection { get; private set; } = new HashSet<int>();
    public ContactDocumentViewState? DocumentState { get; private set; }
    public int? FocusedOrdinal { get; private set; }
    public MessageValidationResult? MessageValidation { get; private set; }
    public MainInteractionState? InteractionState { get; private set; }
    public (Guid Id, SendScope Scope, int Count, DateTimeOffset StartedAt)? BeganBatch { get; private set; }
    public List<RecipientProgress> Progress { get; } = [];
    public bool DelayProgressApplication { get; set; }
    public int MaximumConcurrentProgress => Volatile.Read(ref _maximumConcurrentProgress);
    public TaskCompletionSource ProgressApplicationStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public BatchSummary? Summary { get; private set; }
    public string? Status { get; private set; }
    public string? Error { get; private set; }
    public int CloseBypassCount { get; private set; }

    public SetupInput ReadSetupInput() => SetupInput;

    public void RenderSettings(SettingsDescriptor descriptor, string savedTokenPlaceholder)
    {
        Descriptor = descriptor;
        Placeholder = savedTokenPlaceholder;
    }

    public void RenderDocumentState(ContactDocumentViewState state)
    {
        DocumentState = state;
        Rows = state.Rows;
        AppliedSelection = new HashSet<int>(state.CheckedRecipientOrdinals);
        CheckedRecipientOrdinals = state.CheckedRecipientOrdinals.ToArray();
        HighlightedContactOrdinals = state.HighlightedContactOrdinals.ToArray();
    }
    public void ApplyCheckedRecipients(IReadOnlySet<int> checkedOrdinals) =>
        ApplyChecked(checkedOrdinals);
    public void FocusContact(int ordinal) => FocusedOrdinal = ordinal;
    public void RenderMessageValidation(MessageValidationResult result) => MessageValidation = result;
    public void SetInteractionState(MainInteractionState state) => InteractionState = state;
    public void BeginBatch(Guid batchId, SendScope scope, int confirmedCount, DateTimeOffset startedAt) =>
        BeganBatch = (batchId, scope, confirmedCount, startedAt);
    public void ApplyRecipientProgress(RecipientProgress progress)
    {
        var inFlight = Interlocked.Increment(ref _progressInFlight);
        var maximum = Volatile.Read(ref _maximumConcurrentProgress);
        while (inFlight > maximum)
        {
            var observed = Interlocked.CompareExchange(
                ref _maximumConcurrentProgress,
                inFlight,
                maximum);
            if (observed == maximum)
            {
                break;
            }

            maximum = observed;
        }

        try
        {
            if (DelayProgressApplication)
            {
                ProgressApplicationStarted.TrySetResult();
                if (!_progressRelease.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Timed out waiting to apply recipient progress.");
                }
            }

            lock (Progress)
            {
                Progress.Add(progress);
                Rows = Rows.Select(row =>
                        row.ImportOrdinal == progress.ImportOrdinal
                            ? row with
                            {
                                SendState = progress.State,
                                ProviderMessageId = progress.ProviderMessageId,
                                SafeCode = progress.SafeCode,
                                SafeMessage = progress.SafeMessage
                            }
                            : row)
                    .ToArray();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _progressInFlight);
        }
    }
    public void EndBatch(BatchSummary summary) => Summary = summary;
    public void ShowSafeStatus(string message) => Status = message;
    public void ShowSafeError(string message) => Error = message;
    public void CloseAfterControllerApproval() => CloseBypassCount++;
    public void ReleaseProgressApplication() => _progressRelease.Set();

    private void ApplyChecked(IReadOnlySet<int> checkedOrdinals)
    {
        AppliedSelection = new HashSet<int>(checkedOrdinals);
        CheckedRecipientOrdinals = checkedOrdinals.ToArray();
    }
}
