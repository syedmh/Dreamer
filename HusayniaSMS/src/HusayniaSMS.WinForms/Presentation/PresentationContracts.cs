using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.Presentation;

public sealed record ContactGridRowViewModel(
    int ImportOrdinal,
    bool IsCheckedRecipient,
    bool CanCheckRecipient,
    string Name,
    string Number,
    string ValidationText,
    RecipientSendState? SendState,
    string? ProviderMessageId,
    string? SafeCode,
    string? SafeMessage)
{
    public bool IsSelected => IsCheckedRecipient;
    public bool CanSelect => CanCheckRecipient;
}

public sealed record ContactDocumentViewState(
    string DisplayName,
    bool IsDirty,
    IReadOnlyList<ContactGridRowViewModel> Rows,
    IReadOnlySet<int> CheckedRecipientOrdinals,
    IReadOnlySet<int> HighlightedContactOrdinals);

public sealed record MainInteractionState(
    bool IsBatchActive,
    bool CanEditSetup,
    bool CanSaveSetup,
    bool CanImport,
    bool CanRefresh,
    bool CanAddContact,
    bool CanEditContact,
    bool CanDeleteContacts,
    bool CanSaveCsv,
    bool CanChangeSelection,
    bool CanEditMessage,
    bool CanSendSelected,
    bool CanSendAllValid,
    bool CanCancel)
{
    public MainInteractionState(
        bool isBatchActive,
        bool canEditSetup,
        bool canSaveSetup,
        bool canImport,
        bool canRefresh,
        bool canChangeSelection,
        bool canEditMessage,
        bool canSendSelected,
        bool canSendAllValid,
        bool canCancel)
        : this(
            isBatchActive,
            canEditSetup,
            canSaveSetup,
            canImport,
            canRefresh,
            CanAddContact: false,
            CanEditContact: false,
            CanDeleteContacts: false,
            CanSaveCsv: false,
            canChangeSelection,
            canEditMessage,
            canSendSelected,
            canSendAllValid,
            canCancel)
    {
    }
}

public interface IMainView
{
    string MessageText { get; }
    IReadOnlyList<int> CheckedRecipientOrdinals { get; }
    IReadOnlyList<int> HighlightedContactOrdinals { get; }
    SetupInput ReadSetupInput();
    void RenderSettings(SettingsDescriptor descriptor, string savedTokenPlaceholder);
    void RenderDocumentState(ContactDocumentViewState state);
    void ApplyCheckedRecipients(IReadOnlySet<int> checkedOrdinals);
    void FocusContact(int ordinal);
    void RenderMessageValidation(MessageValidationResult result);
    void SetInteractionState(MainInteractionState state);
    void BeginBatch(Guid batchId, SendScope scope, int confirmedCount, DateTimeOffset startedAt);
    void ApplyRecipientProgress(RecipientProgress progress);
    void EndBatch(BatchSummary summary);
    void ShowSafeStatus(string message);
    void ShowSafeError(string message);
    void CloseAfterControllerApproval();
}

public enum CloseDuringBatchChoice
{
    Stay,
    CancelRemainingAndClose
}

public enum ContactDialogMode
{
    Add = 0,
    Edit = 1
}

public sealed record ContactDialogRequest(
    ContactDialogMode Mode,
    ContactDraft InitialDraft,
    IReadOnlyList<ContactRow> CurrentRows,
    int? EditingOrdinal);

public sealed record ContactDialogResult(string Name, string Number);

public enum PendingAction
{
    Import = 0,
    Refresh = 1,
    Exit = 2
}

public enum UnsavedChangesChoice
{
    Save = 0,
    Discard = 1,
    Cancel = 2
}

public enum ExternalCsvConflictKind
{
    Modified = 0,
    Deleted = 1,
    TargetExists = 2
}

public enum ExternalCsvConflictChoice
{
    ReloadExternal = 0,
    OverwriteThisVersion = 1,
    Recreate = 2,
    SaveAs = 3,
    ChooseAnother = 4,
    Cancel = 5
}

public enum SaveFailureChoice
{
    SaveAs = 0,
    Cancel = 1
}

public interface IUserDialogs
{
    string? SelectCsvPath();
    bool ConfirmSend(SendScope scope, int recipientCount, bool isSafeDemo);
    CloseDuringBatchChoice ConfirmCloseDuringBatch();
    ContactDialogResult? ShowContactDialog(
        ContactDialogRequest request,
        IContactDraftValidator validator);
    bool ConfirmDeleteContacts(int count);
    UnsavedChangesChoice ConfirmUnsavedChanges(PendingAction action, bool canSave);
    string? SelectCsvSavePath(string? suggestedPath);
    ExternalCsvConflictChoice ResolveExternalCsvConflict(
        ExternalCsvConflictKind kind,
        string fileName);
    SaveFailureChoice ResolveSaveFailure(
        ContactCsvSaveStatus status,
        string fileName);
}
