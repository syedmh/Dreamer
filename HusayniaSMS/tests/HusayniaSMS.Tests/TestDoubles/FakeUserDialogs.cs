using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class FakeUserDialogs : IUserDialogs
{
    public string? CsvPath { get; set; }
    public bool ConfirmSendResult { get; set; } = true;
    public CloseDuringBatchChoice CloseChoice { get; set; } = CloseDuringBatchChoice.Stay;
    public int CsvSelectionCount { get; private set; }
    public int SendConfirmationCount { get; private set; }
    public int CloseConfirmationCount { get; private set; }
    public (SendScope Scope, int Count, bool SafeDemo)? LastConfirmation { get; private set; }
    public ContactDialogResult? ContactResult { get; set; }
    public bool DeleteConfirmationResult { get; set; }
    public UnsavedChangesChoice UnsavedChoice { get; set; } = UnsavedChangesChoice.Cancel;
    public string? CsvSavePath { get; set; }
    public ExternalCsvConflictChoice ConflictChoice { get; set; } =
        ExternalCsvConflictChoice.Cancel;
    public SaveFailureChoice FailureChoice { get; set; } = SaveFailureChoice.Cancel;
    public ContactDialogRequest? LastContactRequest { get; private set; }
    public int DeleteConfirmationCount { get; private set; }
    public int UnsavedConfirmationCount { get; private set; }
    public int SavePathSelectionCount { get; private set; }
    public int? LastDeleteCount { get; private set; }
    public (PendingAction Action, bool CanSave)? LastUnsavedPrompt { get; private set; }
    public List<(ExternalCsvConflictKind Kind, string FileName)> ConflictPrompts { get; } = [];
    public List<(ContactCsvSaveStatus Status, string FileName)> FailurePrompts { get; } = [];
    public Queue<ContactDialogResult?> ContactResults { get; } = [];
    public Queue<UnsavedChangesChoice> UnsavedChoices { get; } = [];
    public Queue<string?> SavePaths { get; } = [];
    public Queue<ExternalCsvConflictChoice> ConflictChoices { get; } = [];
    public Queue<SaveFailureChoice> FailureChoices { get; } = [];

    public string? SelectCsvPath()
    {
        CsvSelectionCount++;
        return CsvPath;
    }

    public bool ConfirmSend(SendScope scope, int recipientCount, bool isSafeDemo)
    {
        SendConfirmationCount++;
        LastConfirmation = (scope, recipientCount, isSafeDemo);
        return ConfirmSendResult;
    }

    public CloseDuringBatchChoice ConfirmCloseDuringBatch()
    {
        CloseConfirmationCount++;
        return CloseChoice;
    }

    public ContactDialogResult? ShowContactDialog(
        ContactDialogRequest request,
        IContactDraftValidator validator)
    {
        LastContactRequest = request;
        return ContactResults.Count > 0 ? ContactResults.Dequeue() : ContactResult;
    }

    public bool ConfirmDeleteContacts(int count)
    {
        DeleteConfirmationCount++;
        LastDeleteCount = count;
        return DeleteConfirmationResult;
    }

    public UnsavedChangesChoice ConfirmUnsavedChanges(PendingAction action, bool canSave)
    {
        UnsavedConfirmationCount++;
        LastUnsavedPrompt = (action, canSave);
        return UnsavedChoices.Count > 0 ? UnsavedChoices.Dequeue() : UnsavedChoice;
    }

    public string? SelectCsvSavePath(string? suggestedPath)
    {
        SavePathSelectionCount++;
        return SavePaths.Count > 0 ? SavePaths.Dequeue() : CsvSavePath;
    }

    public ExternalCsvConflictChoice ResolveExternalCsvConflict(
        ExternalCsvConflictKind kind,
        string fileName)
    {
        ConflictPrompts.Add((kind, fileName));
        return ConflictChoices.Count > 0 ? ConflictChoices.Dequeue() : ConflictChoice;
    }

    public SaveFailureChoice ResolveSaveFailure(
        ContactCsvSaveStatus status,
        string fileName)
    {
        FailurePrompts.Add((status, fileName));
        return FailureChoices.Count > 0 ? FailureChoices.Dequeue() : FailureChoice;
    }
}
