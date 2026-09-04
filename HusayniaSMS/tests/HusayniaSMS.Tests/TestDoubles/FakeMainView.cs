using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeMainView : IMainView
{
    public string MessageText { get; set; } = string.Empty;
    public IReadOnlyList<int> SelectedOrdinals { get; set; } = Array.Empty<int>();
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
    public MessageValidationResult? MessageValidation { get; private set; }
    public MainInteractionState? InteractionState { get; private set; }
    public (Guid Id, SendScope Scope, int Count, DateTimeOffset StartedAt)? BeganBatch { get; private set; }
    public List<RecipientProgress> Progress { get; } = [];
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

    public void ReplaceContacts(IReadOnlyList<ContactGridRowViewModel> rows) => Rows = rows;
    public void ApplySelection(IReadOnlySet<int> selectedOrdinals) =>
        AppliedSelection = new HashSet<int>(selectedOrdinals);
    public void RenderMessageValidation(MessageValidationResult result) => MessageValidation = result;
    public void SetInteractionState(MainInteractionState state) => InteractionState = state;
    public void BeginBatch(Guid batchId, SendScope scope, int confirmedCount, DateTimeOffset startedAt) =>
        BeganBatch = (batchId, scope, confirmedCount, startedAt);
    public void ApplyRecipientProgress(RecipientProgress progress)
    {
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
    public void EndBatch(BatchSummary summary) => Summary = summary;
    public void ShowSafeStatus(string message) => Status = message;
    public void ShowSafeError(string message) => Error = message;
    public void CloseWithBypass() => CloseBypassCount++;
}
