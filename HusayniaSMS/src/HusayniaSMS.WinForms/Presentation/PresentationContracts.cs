using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.Presentation;

public sealed record ContactGridRowViewModel(
    int ImportOrdinal,
    bool IsSelected,
    bool CanSelect,
    string Name,
    string Number,
    string ValidationText,
    RecipientSendState? SendState,
    string? ProviderMessageId,
    string? SafeCode,
    string? SafeMessage);

public sealed record MainInteractionState(
    bool IsBatchActive,
    bool CanEditSetup,
    bool CanSaveSetup,
    bool CanImport,
    bool CanRefresh,
    bool CanChangeSelection,
    bool CanEditMessage,
    bool CanSendSelected,
    bool CanSendAllValid,
    bool CanCancel);

public interface IMainView
{
    string MessageText { get; }
    IReadOnlyList<int> SelectedOrdinals { get; }
    SetupInput ReadSetupInput();
    void RenderSettings(SettingsDescriptor descriptor, string savedTokenPlaceholder);
    void ReplaceContacts(IReadOnlyList<ContactGridRowViewModel> rows);
    void ApplySelection(IReadOnlySet<int> selectedOrdinals);
    void RenderMessageValidation(MessageValidationResult result);
    void SetInteractionState(MainInteractionState state);
    void BeginBatch(Guid batchId, SendScope scope, int confirmedCount, DateTimeOffset startedAt);
    void ApplyRecipientProgress(RecipientProgress progress);
    void EndBatch(BatchSummary summary);
    void ShowSafeStatus(string message);
    void ShowSafeError(string message);
    void CloseWithBypass();
}

public enum CloseDuringBatchChoice
{
    Stay,
    CancelRemainingAndClose
}

public interface IUserDialogs
{
    string? SelectCsvPath();
    bool ConfirmSend(SendScope scope, int recipientCount, bool isSafeDemo);
    CloseDuringBatchChoice ConfirmCloseDuringBatch();
}
