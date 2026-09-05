using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;

namespace HusayniaSMS.WinForms.Presentation;

public sealed class MainController
{
    public const string SavedTokenPlaceholder = "••••••••••••";

    private readonly IMainView _view;
    private readonly IUserDialogs _dialogs;
    private readonly IContactCsvStore _csvStore;
    private readonly ISettingsService _settingsService;
    private readonly IMessageValidator _messageValidator;
    private readonly IBatchSendCoordinator _batchCoordinator;
    private readonly IContactDraftValidator _draftValidator;
    private readonly IContactRowValidator _rowValidator;
    private readonly TimeProvider _timeProvider;
    private readonly bool _isSafeDemo;
    private ContactDocumentState _document = ContactDocumentState.CreateUntitled();
    private HashSet<int> _checkedRecipients = [];
    private HashSet<int> _highlightedContacts = [];
    private readonly Dictionary<int, RecipientProgress> _results = [];
    private string? _rememberedCsvPath;
    private CancellationTokenSource? _batchCancellation;
    private TaskCompletionSource _batchSettled = CompletedSource();
    private int _interactionInProgress;
    private bool _closeRequestInProgress;
    private bool _closeApprovalIssued;

    public MainController(
        IMainView view,
        IUserDialogs dialogs,
        IContactCsvStore csvStore,
        ISettingsService settingsService,
        IMessageValidator messageValidator,
        IBatchSendCoordinator batchCoordinator,
        TimeProvider timeProvider,
        bool isSafeDemo,
        IContactDraftValidator draftValidator,
        IContactRowValidator rowValidator)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _csvStore = csvStore ?? throw new ArgumentNullException(nameof(csvStore));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _messageValidator = messageValidator ?? throw new ArgumentNullException(nameof(messageValidator));
        _batchCoordinator = batchCoordinator ?? throw new ArgumentNullException(nameof(batchCoordinator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _isSafeDemo = isSafeDemo;
        _draftValidator = draftValidator ?? throw new ArgumentNullException(nameof(draftValidator));
        _rowValidator = rowValidator ?? throw new ArgumentNullException(nameof(rowValidator));
    }

    public MainController(
        IMainView view,
        IUserDialogs dialogs,
        IContactCsvStore csvStore,
        ISettingsService settingsService,
        IMessageValidator messageValidator,
        IBatchSendCoordinator batchCoordinator,
        TimeProvider timeProvider,
        bool isSafeDemo)
        : this(
            view,
            dialogs,
            csvStore,
            settingsService,
            messageValidator,
            batchCoordinator,
            timeProvider,
            isSafeDemo,
            new ContactDraftValidator(new E164PhoneNumberValidator()),
            new ContactRowValidator(new E164PhoneNumberValidator()))
    {
    }

    public bool IsBatchActive { get; private set; }

    public async Task InitializeAsync()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            var descriptor = await _settingsService.LoadDescriptorAsync(CancellationToken.None);
            _rememberedCsvPath = descriptor.LastCsvPath;
            _view.RenderSettings(descriptor, SavedTokenPlaceholder);
            _view.RenderMessageValidation(_messageValidator.Validate(_view.MessageText));
            RenderDocument();
            _view.ShowSafeStatus(descriptor.TokenState == TokenState.Available
                ? "Twilio setup is available."
                : descriptor.TokenState == TokenState.Unavailable
                    ? "Saved setup cannot provide a usable auth token. Re-enter it."
                    : "Twilio setup is incomplete.");
        }
        catch
        {
            _view.ShowSafeError("The application could not initialize all setup information.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public async Task SaveSetupAsync()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            var result = await _settingsService.SaveAsync(
                _view.ReadSetupInput(),
                CancellationToken.None);
            if (result.Status == SaveSettingsStatus.ValidationFailed)
            {
                _view.ShowSafeError(string.Join(
                    Environment.NewLine,
                    result.Errors.Select(error => error.Message)));
                return;
            }

            if (result.Status != SaveSettingsStatus.Saved)
            {
                _view.ShowSafeError(result.SafeMessage ?? "Twilio setup could not be saved.");
                return;
            }

            var descriptor = await _settingsService.LoadDescriptorAsync(CancellationToken.None);
            _view.RenderSettings(descriptor, SavedTokenPlaceholder);
            _view.ShowSafeStatus(result.SafeMessage ?? "Twilio setup was saved securely.");
        }
        catch
        {
            _view.ShowSafeError("Twilio setup could not be saved because of an unexpected error.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public async Task ImportContactsAsync()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            if (!await ConfirmPendingActionAsync(PendingAction.Import).ConfigureAwait(true))
            {
                return;
            }

            string? path;
            try
            {
                path = _dialogs.SelectCsvPath();
            }
            catch
            {
                _view.ShowSafeError("The CSV file chooser could not be opened.");
                return;
            }

            if (path is null)
            {
                return;
            }

            var result = await _csvStore.LoadAsync(path, CancellationToken.None);
            if (!TryValidateSuccessfulLoad(result, "CSV import failed."))
            {
                return;
            }

            var savePath = await _settingsService.SaveLastCsvPathAsync(
                result.FullPath!,
                CancellationToken.None);
            if (savePath.Status != SaveCsvPathStatus.Saved)
            {
                var message = savePath.SafeMessage ??
                    "The CSV was read, but its path could not be saved.";
                _view.ShowSafeError(
                    $"{message} Existing contacts and the remembered CSV path were preserved.");
                return;
            }

            _rememberedCsvPath = result.FullPath;
            ReplaceWithLoadedDocument(result, preserveRefreshState: false);
            _view.ShowSafeStatus(
                $"Imported {_document.Rows.Count} contact(s); {_document.Rows.Count(row => row.IsEligible)} valid.");
        }
        catch
        {
            _view.ShowSafeError("CSV import failed because of an unexpected error.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public async Task RefreshContactsAsync()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            if (!await ConfirmPendingActionAsync(PendingAction.Refresh).ConfigureAwait(true))
            {
                return;
            }

            await RefreshCoreAsync(showSuccessStatus: true).ConfigureAwait(true);
        }
        catch
        {
            _view.ShowSafeError("CSV refresh failed because of an unexpected error.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public void AddContact()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            var result = _dialogs.ShowContactDialog(
                new(
                    ContactDialogMode.Add,
                    new(null, null),
                    _document.Rows,
                    null),
                _draftValidator);
            if (result is null)
            {
                return;
            }

            var draft = _draftValidator.Validate(
                new(result.Name, result.Number),
                _document.Rows,
                null);
            if (!draft.IsValid)
            {
                _view.ShowSafeError(FormatValidationErrors(draft.Errors));
                return;
            }

            var ordinal = _document.NextOrdinal;
            _document = _document.Add(draft, _rowValidator);
            NormalizeCheckedRecipients();
            _highlightedContacts = [ordinal];
            RenderDocument();
            _view.FocusContact(ordinal);
            _view.ShowSafeStatus("Contact added. Save CSV to persist changes.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public void EditContact()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            SynchronizeHighlightedFromView();
            if (_highlightedContacts.Count != 1)
            {
                _view.ShowSafeError("Highlight exactly one contact to edit.");
                return;
            }

            var ordinal = _highlightedContacts.Single();
            var current = _document.Rows.SingleOrDefault(
                row => row.ImportOrdinal == ordinal);
            if (current is null)
            {
                _view.ShowSafeError("The highlighted contact is no longer available.");
                return;
            }

            var result = _dialogs.ShowContactDialog(
                new(
                    ContactDialogMode.Edit,
                    new(current.Name, current.Number),
                    _document.Rows,
                    ordinal),
                _draftValidator);
            if (result is null)
            {
                return;
            }

            var draft = _draftValidator.Validate(
                new(result.Name, result.Number),
                _document.Rows,
                ordinal);
            if (!draft.IsValid)
            {
                _view.ShowSafeError(FormatValidationErrors(draft.Errors));
                return;
            }

            var numberChanged = !StringComparer.Ordinal.Equals(
                current.Number,
                draft.Number);
            _document = _document.Edit(ordinal, draft, _rowValidator);
            if (numberChanged)
            {
                _checkedRecipients.Remove(ordinal);
                _results.Remove(ordinal);
            }

            NormalizeCheckedRecipients();
            _highlightedContacts = [ordinal];
            RenderDocument();
            _view.FocusContact(ordinal);
            _view.ShowSafeStatus("Contact updated. Save CSV to persist changes.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public void DeleteSelectedContacts()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            SynchronizeHighlightedFromView();
            if (_highlightedContacts.Count == 0)
            {
                _view.ShowSafeError("Highlight one or more contacts to delete.");
                return;
            }

            var ordered = _document.Rows
                .Select((row, index) => (row, index))
                .Where(item => _highlightedContacts.Contains(item.row.ImportOrdinal))
                .ToArray();
            if (ordered.Length == 0 || !_dialogs.ConfirmDeleteContacts(ordered.Length))
            {
                return;
            }

            var firstDeletedIndex = ordered.Min(item => item.index);
            var deleted = ordered.Select(item => item.row.ImportOrdinal).ToHashSet();
            _document = _document.Delete(deleted, _rowValidator);
            _checkedRecipients.ExceptWith(deleted);
            foreach (var ordinal in deleted)
            {
                _results.Remove(ordinal);
            }

            NormalizeCheckedRecipients();
            _highlightedContacts.Clear();
            if (_document.Rows.Count > 0)
            {
                var survivorIndex = Math.Min(firstDeletedIndex, _document.Rows.Count - 1);
                _highlightedContacts.Add(_document.Rows[survivorIndex].ImportOrdinal);
            }

            RenderDocument();
            if (_highlightedContacts.Count == 1)
            {
                _view.FocusContact(_highlightedContacts.Single());
            }

            _view.ShowSafeStatus(
                $"Deleted {deleted.Count} contact(s). Save CSV to persist changes.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public async Task SaveContactsAsync()
    {
        if (!TryBeginInteraction())
        {
            return;
        }

        UpdateInteractionState();
        try
        {
            await SaveContactsCoreAsync().ConfigureAwait(true);
        }
        catch
        {
            _view.ShowSafeError("CSV save failed because of an unexpected error.");
        }
        finally
        {
            EndInteraction();
            UpdateInteractionState();
        }
    }

    public void CheckedRecipientsChanged()
    {
        if (IsBatchActive || IsInteractionInProgress)
        {
            return;
        }

        SynchronizeCheckedFromView();
        UpdateInteractionState();
    }

    public void GridHighlightChanged()
    {
        if (IsBatchActive || IsInteractionInProgress)
        {
            return;
        }

        SynchronizeHighlightedFromView();
        UpdateInteractionState();
    }

    public void SelectAllEligible()
    {
        if (IsBatchActive || IsInteractionInProgress)
        {
            return;
        }

        _checkedRecipients = _document.Rows.Where(row => row.IsEligible)
            .Select(row => row.ImportOrdinal)
            .ToHashSet();
        _view.ApplyCheckedRecipients(_checkedRecipients);
        UpdateInteractionState();
    }

    public void ClearSelection()
    {
        if (IsBatchActive || IsInteractionInProgress)
        {
            return;
        }

        _checkedRecipients.Clear();
        _view.ApplyCheckedRecipients(_checkedRecipients);
        UpdateInteractionState();
    }

    public void MessageChanged()
    {
        _view.RenderMessageValidation(_messageValidator.Validate(_view.MessageText));
        UpdateInteractionState();
    }

    public async Task SendAsync(SendScope scope)
    {
        if (!TryBeginInteraction())
        {
            _view.ShowSafeStatus("A send operation is already active.");
            return;
        }

        SerializedProgress<RecipientProgress>? progress = null;
        UpdateInteractionState();
        try
        {
            var message = _view.MessageText;
            SynchronizeCheckedFromView();
            var messageValidation = _messageValidator.Validate(message);
            _view.RenderMessageValidation(messageValidation);
            if (!messageValidation.IsValid)
            {
                _view.ShowSafeError(messageValidation.ErrorMessage!);
                return;
            }

            var credentialsResult =
                await _settingsService.LoadCredentialsAsync(CancellationToken.None);
            if (credentialsResult.Status != CredentialLoadStatus.Ready ||
                credentialsResult.Credentials is null)
            {
                _view.ShowSafeError(credentialsResult.SafeMessage
                    ?? "Complete valid Twilio setup before sending.");
                return;
            }

            var snapshot = CreateSnapshot(scope);
            if (snapshot.Length == 0)
            {
                _view.ShowSafeError(scope == SendScope.Selected
                    ? "Select at least one valid contact before sending."
                    : "Import or add at least one valid contact before sending.");
                return;
            }

            if (!_dialogs.ConfirmSend(scope, snapshot.Length, _isSafeDemo))
            {
                _view.ShowSafeStatus("Send canceled before any recipient was submitted.");
                return;
            }

            var batchId = Guid.NewGuid();
            var startedAt = _timeProvider.GetUtcNow();
            var request = new SmsBatchRequest(
                batchId,
                scope,
                Array.AsReadOnly(snapshot),
                message,
                credentialsResult.Credentials);

            _batchCancellation = new CancellationTokenSource();
            _batchSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IsBatchActive = true;
            _checkedRecipients = snapshot
                .Select(recipient => recipient.ImportOrdinal)
                .ToHashSet();
            _results.Clear();
            RenderDocument();
            _view.BeginBatch(batchId, scope, snapshot.Length, startedAt);
            UpdateInteractionState();

            progress = new(
                SynchronizationContext.Current,
                ApplyProgress);
            var run = await _batchCoordinator.TryRunAsync(
                request,
                progress,
                _batchCancellation.Token);
            await progress.CompleteAsync().ConfigureAwait(true);
            if (run.Status == BatchStartStatus.RejectedAlreadyActive)
            {
                _view.ShowSafeError("A send batch is already active.");
            }
            else if (run.Summary is not null)
            {
                _view.EndBatch(run.Summary);
            }
        }
        catch
        {
            if (progress is not null)
            {
                try
                {
                    await progress.CompleteAsync().ConfigureAwait(true);
                }
                catch
                {
                    // The safe controller-level error below remains authoritative.
                }
            }

            _view.ShowSafeError(
                "The send operation stopped because of an unexpected error. No automatic retry was attempted.");
        }
        finally
        {
            if (IsBatchActive)
            {
                IsBatchActive = false;
                _batchCancellation?.Dispose();
                _batchCancellation = null;
            }

            EndInteraction();
            UpdateInteractionState();
            _batchSettled.TrySetResult();
        }
    }

    public void CancelActiveBatch()
    {
        if (IsBatchActive)
        {
            _batchCancellation?.Cancel();
            _view.ShowSafeStatus(
                "Cancellation requested. The in-flight recipient may still settle.");
        }
    }

    public async Task RequestCloseAsync()
    {
        if (_closeApprovalIssued || _closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        try
        {
            if (IsBatchActive)
            {
                if (_dialogs.ConfirmCloseDuringBatch() == CloseDuringBatchChoice.Stay)
                {
                    return;
                }

                CancelActiveBatch();
                await _batchSettled.Task.ConfigureAwait(true);
            }

            if (!TryBeginInteraction())
            {
                _view.ShowSafeStatus(
                    "Finish the current operation before closing the application.");
                return;
            }

            UpdateInteractionState();
            try
            {
                if (!await ConfirmPendingActionAsync(PendingAction.Exit).ConfigureAwait(true))
                {
                    return;
                }

                _closeApprovalIssued = true;
                _view.CloseAfterControllerApproval();
            }
            finally
            {
                EndInteraction();
                UpdateInteractionState();
            }
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }

    public Task HandleActiveCloseRequestAsync() => RequestCloseAsync();

    private async Task<bool> SaveContactsCoreAsync()
    {
        if (!_document.IsDirty)
        {
            return true;
        }

        var firstInvalid = _document.Rows.FirstOrDefault(row => !row.IsEligible);
        if (firstInvalid is not null)
        {
            _view.FocusContact(firstInvalid.ImportOrdinal);
            _view.ShowSafeError(
                "Correct or delete every invalid or duplicate contact before saving.");
            return false;
        }

        var path = _document.Path;
        var expectedVersion = _document.Version;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = _dialogs.SelectCsvSavePath(null);
            expectedVersion = null;
            if (path is null)
            {
                return false;
            }
        }

        while (true)
        {
            var result = await _csvStore.SaveAsync(
                new(path, _document.Rows, expectedVersion),
                CancellationToken.None);
            if (result.Status == ContactCsvSaveStatus.Saved &&
                result.FullPath is not null &&
                result.SavedVersion is not null)
            {
                _document = _document.MarkSaved(result.FullPath, result.SavedVersion);
                _rememberedCsvPath = result.FullPath;
                RenderDocument();
                var remembered = await _settingsService.SaveLastCsvPathAsync(
                    result.FullPath,
                    CancellationToken.None);
                var warnings = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.SafeDiagnostic))
                {
                    warnings.Add(result.SafeDiagnostic);
                }

                if (remembered.Status != SaveCsvPathStatus.Saved)
                {
                    warnings.Add(
                        "Contacts were saved, but the CSV path may not be remembered after restart.");
                }

                if (warnings.Count == 0)
                {
                    _view.ShowSafeStatus("Contacts were saved.");
                }
                else
                {
                    _view.ShowSafeError(string.Join(" ", warnings));
                }

                return true;
            }

            if (result.Status == ContactCsvSaveStatus.InvalidDocument)
            {
                var invalid = _document.Rows.FirstOrDefault(row => !row.IsEligible);
                if (invalid is not null)
                {
                    _view.FocusContact(invalid.ImportOrdinal);
                    _view.ShowSafeError(
                        "Correct or delete every invalid or duplicate contact before saving.");
                }
                else
                {
                    _view.ShowSafeError(
                        result.SafeDiagnostic ?? "CSV document cannot be saved.");
                }

                return false;
            }

            if (result.Status is ContactCsvSaveStatus.ConflictModified or
                ContactCsvSaveStatus.ConflictDeleted or
                ContactCsvSaveStatus.TargetExists)
            {
                var kind = result.Status switch
                {
                    ContactCsvSaveStatus.ConflictModified =>
                        ExternalCsvConflictKind.Modified,
                    ContactCsvSaveStatus.ConflictDeleted =>
                        ExternalCsvConflictKind.Deleted,
                    _ => ExternalCsvConflictKind.TargetExists
                };
                var choice = _dialogs.ResolveExternalCsvConflict(
                    kind,
                    Path.GetFileName(path));
                switch (choice)
                {
                    case ExternalCsvConflictChoice.ReloadExternal
                        when kind == ExternalCsvConflictKind.Modified:
                        await RefreshFromPathCoreAsync(
                            result.FullPath ?? path,
                            showSuccessStatus: true).ConfigureAwait(true);
                        return false;
                    case ExternalCsvConflictChoice.OverwriteThisVersion
                        when result.CurrentVersion is not null:
                        expectedVersion = result.CurrentVersion;
                        continue;
                    case ExternalCsvConflictChoice.Recreate
                        when kind == ExternalCsvConflictKind.Deleted:
                        expectedVersion = null;
                        continue;
                    case ExternalCsvConflictChoice.SaveAs:
                    case ExternalCsvConflictChoice.ChooseAnother:
                        path = _dialogs.SelectCsvSavePath(path);
                        expectedVersion = null;
                        if (path is null)
                        {
                            return false;
                        }

                        continue;
                    default:
                        return false;
                }
            }

            if (result.Status is ContactCsvSaveStatus.AccessDenied or
                ContactCsvSaveStatus.IoFailure or
                ContactCsvSaveStatus.AtomicReplaceUnavailable)
            {
                if (_dialogs.ResolveSaveFailure(
                        result.Status,
                        Path.GetFileName(path)) != SaveFailureChoice.SaveAs)
                {
                    _view.ShowSafeError(
                        result.SafeDiagnostic ?? "CSV file could not be saved.");
                    return false;
                }

                path = _dialogs.SelectCsvSavePath(path);
                expectedVersion = null;
                if (path is null)
                {
                    return false;
                }

                continue;
            }

            _view.ShowSafeError(result.SafeDiagnostic ?? "CSV file could not be saved.");
            return false;
        }
    }

    private async Task<bool> ConfirmPendingActionAsync(PendingAction action)
    {
        if (!_document.IsDirty)
        {
            return true;
        }

        var canSave = _document.Rows.All(row => row.IsEligible);
        var choice = _dialogs.ConfirmUnsavedChanges(action, canSave);
        return choice switch
        {
            UnsavedChangesChoice.Discard => true,
            UnsavedChangesChoice.Save when canSave =>
                await SaveContactsCoreAsync().ConfigureAwait(true),
            UnsavedChangesChoice.Save => FocusInvalidAndRejectPendingAction(),
            _ => false
        };
    }

    private bool FocusInvalidAndRejectPendingAction()
    {
        var invalid = _document.Rows.FirstOrDefault(row => !row.IsEligible);
        if (invalid is not null)
        {
            _view.FocusContact(invalid.ImportOrdinal);
        }

        _view.ShowSafeError(
            "Correct or delete every invalid or duplicate contact before saving.");
        return false;
    }

    private async Task<bool> RefreshCoreAsync(bool showSuccessStatus)
    {
        var path = _document.Path ?? _rememberedCsvPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _view.ShowSafeError(
                "No CSV file is available. Use Import CSV or Save CSV first.");
            return false;
        }

        return await RefreshFromPathCoreAsync(path, showSuccessStatus).ConfigureAwait(true);
    }

    private async Task<bool> RefreshFromPathCoreAsync(
        string path,
        bool showSuccessStatus)
    {
        SynchronizeCheckedFromView();
        SynchronizeHighlightedFromView();
        var checkedNumbers = _document.Rows
            .Where(row => row.IsEligible &&
                _checkedRecipients.Contains(row.ImportOrdinal))
            .Select(row => row.Number)
            .ToHashSet(StringComparer.Ordinal);
        var priorCheckedCount = checkedNumbers.Count;
        var highlightedNumber = _highlightedContacts.Count == 1
            ? _document.Rows.FirstOrDefault(
                row => row.ImportOrdinal == _highlightedContacts.Single())?.Number
            : null;

        var result = await _csvStore.LoadAsync(path, CancellationToken.None);
        if (!TryValidateSuccessfulLoad(result, "CSV refresh failed."))
        {
            return false;
        }

        ReplaceWithLoadedDocument(
            result,
            preserveRefreshState: true,
            checkedNumbers,
            highlightedNumber);
        if (showSuccessStatus)
        {
            var selectionStatus = priorCheckedCount == 0
                ? "Selection remains clear."
                : $"Preserved {_checkedRecipients.Count} of {priorCheckedCount} selected recipient(s) that remain eligible.";
            _view.ShowSafeStatus(
                $"Refreshed {_document.Rows.Count} contact(s); {_document.Rows.Count(row => row.IsEligible)} valid. {selectionStatus}");
        }

        return true;
    }

    private bool TryValidateSuccessfulLoad(
        ContactCsvLoadResult result,
        string fallbackMessage)
    {
        if (result.Status == CsvImportStatus.Success &&
            result.FullPath is not null &&
            result.Version is not null)
        {
            return true;
        }

        if (result.Status != CsvImportStatus.Canceled)
        {
            _view.ShowSafeError(result.SafeDiagnostic ?? fallbackMessage);
        }

        return false;
    }

    private void ReplaceWithLoadedDocument(
        ContactCsvLoadResult result,
        bool preserveRefreshState,
        IReadOnlySet<string>? checkedNumbers = null,
        string? highlightedNumber = null)
    {
        _document = ContactDocumentState.FromLoaded(
            result.FullPath!,
            result.Version!,
            result.Rows);
        _results.Clear();
        _checkedRecipients = preserveRefreshState && checkedNumbers is not null
            ? _document.Rows
                .Where(row => row.IsEligible && checkedNumbers.Contains(row.Number))
                .Select(row => row.ImportOrdinal)
                .ToHashSet()
            : [];
        _highlightedContacts = preserveRefreshState && highlightedNumber is not null
            ? _document.Rows
                .Where(row => StringComparer.Ordinal.Equals(row.Number, highlightedNumber))
                .Take(1)
                .Select(row => row.ImportOrdinal)
                .ToHashSet()
            : [];
        RenderDocument();
    }

    private void SynchronizeCheckedFromView()
    {
        var eligible = _document.Rows.Where(row => row.IsEligible)
            .Select(row => row.ImportOrdinal)
            .ToHashSet();
        _checkedRecipients = _view.CheckedRecipientOrdinals
            .Where(eligible.Contains)
            .ToHashSet();
    }

    private void SynchronizeHighlightedFromView()
    {
        var existing = _document.Rows.Select(row => row.ImportOrdinal).ToHashSet();
        _highlightedContacts = _view.HighlightedContactOrdinals
            .Where(existing.Contains)
            .ToHashSet();
    }

    private void NormalizeCheckedRecipients()
    {
        var eligible = _document.Rows.Where(row => row.IsEligible)
            .Select(row => row.ImportOrdinal)
            .ToHashSet();
        _checkedRecipients.IntersectWith(eligible);
    }

    private RecipientSnapshot[] CreateSnapshot(SendScope scope) =>
        _document.Rows
            .Where(row => row.IsEligible &&
                (scope == SendScope.AllValid ||
                    _checkedRecipients.Contains(row.ImportOrdinal)))
            .Select(row => new RecipientSnapshot(
                row.ImportOrdinal,
                row.Name,
                row.Number))
            .ToArray();

    private void ApplyProgress(RecipientProgress progress)
    {
        _results[progress.ImportOrdinal] = progress;
        _view.ApplyRecipientProgress(progress);
    }

    private void RenderDocument()
    {
        var rows = _document.Rows.Select(row =>
        {
            _results.TryGetValue(row.ImportOrdinal, out var result);
            return new ContactGridRowViewModel(
                row.ImportOrdinal,
                _checkedRecipients.Contains(row.ImportOrdinal),
                row.IsEligible,
                row.Name,
                row.Number,
                row.IsEligible
                    ? "Valid"
                    : string.Join(
                        Environment.NewLine,
                        row.Errors.Select(ContactValidationMessages.GetMessage)),
                result?.State,
                result?.ProviderMessageId,
                result?.SafeCode,
                result?.SafeMessage);
        }).ToArray();
        _view.RenderDocumentState(new(
            DisplayName(),
            _document.IsDirty,
            rows,
            _checkedRecipients,
            _highlightedContacts));
    }

    private string DisplayName() => string.IsNullOrWhiteSpace(_document.Path)
        ? "Unsaved contacts"
        : Path.GetFileName(_document.Path);

    private static string FormatValidationErrors(
        IReadOnlyList<ContactErrorCode> errors) =>
        string.Join(Environment.NewLine, errors.Select(ContactValidationMessages.GetMessage));

    private void UpdateInteractionState()
    {
        var mutable = !IsBatchActive && !IsInteractionInProgress;
        var validCount = _document.Rows.Count(row => row.IsEligible);
        _view.SetInteractionState(new(
            IsBatchActive,
            CanEditSetup: mutable,
            CanSaveSetup: mutable,
            CanImport: mutable,
            CanRefresh: mutable,
            CanAddContact: mutable,
            CanEditContact: mutable && _highlightedContacts.Count == 1,
            CanDeleteContacts: mutable && _highlightedContacts.Count > 0,
            CanSaveCsv: mutable &&
                _document.IsDirty &&
                _document.Rows.All(row => row.IsEligible),
            CanChangeSelection: mutable && validCount > 0,
            CanEditMessage: mutable,
            CanSendSelected: mutable && validCount > 0,
            CanSendAllValid: mutable && validCount > 0,
            CanCancel: IsBatchActive));
    }

    private bool IsInteractionInProgress =>
        Volatile.Read(ref _interactionInProgress) != 0;

    private bool TryBeginInteraction() =>
        !IsBatchActive &&
        Interlocked.CompareExchange(ref _interactionInProgress, 1, 0) == 0;

    private void EndInteraction() => Volatile.Write(ref _interactionInProgress, 0);

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class SerializedProgress<T>(
        SynchronizationContext? synchronizationContext,
        Action<T> apply)
        : IProgress<T>
    {
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;
        private bool _accepting = true;

        public void Report(T value)
        {
            lock (_gate)
            {
                if (!_accepting)
                {
                    throw new InvalidOperationException(
                        "Progress cannot be reported after batch completion.");
                }

                _tail = ApplyAfterAsync(_tail, value);
            }
        }

        public Task CompleteAsync()
        {
            lock (_gate)
            {
                _accepting = false;
                return _tail;
            }
        }

        private async Task ApplyAfterAsync(Task previous, T value)
        {
            await previous.ConfigureAwait(false);
            if (synchronizationContext is null ||
                ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
            {
                apply(value);
                return;
            }

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            synchronizationContext.Post(
                _ =>
                {
                    try
                    {
                        apply(value);
                        completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                },
                null);
            await completion.Task.ConfigureAwait(false);
        }
    }
}
