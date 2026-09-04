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
    private readonly IContactCsvImporter _csvImporter;
    private readonly ISettingsService _settingsService;
    private readonly IMessageValidator _messageValidator;
    private readonly IBatchSendCoordinator _batchCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly bool _isSafeDemo;
    private IReadOnlyList<ContactRow> _contacts = Array.Empty<ContactRow>();
    private HashSet<int> _selection = [];
    private string? _lastCsvPath;
    private CancellationTokenSource? _batchCancellation;
    private TaskCompletionSource _batchSettled = CompletedSource();
    private int _interactionInProgress;
    private bool _closeWhenSettled;
    private bool _closeBypassUsed;
    private bool _closeRequestInProgress;

    public MainController(
        IMainView view,
        IUserDialogs dialogs,
        IContactCsvImporter csvImporter,
        ISettingsService settingsService,
        IMessageValidator messageValidator,
        IBatchSendCoordinator batchCoordinator,
        TimeProvider timeProvider,
        bool isSafeDemo)
    {
        _view = view;
        _dialogs = dialogs;
        _csvImporter = csvImporter;
        _settingsService = settingsService;
        _messageValidator = messageValidator;
        _batchCoordinator = batchCoordinator;
        _timeProvider = timeProvider;
        _isSafeDemo = isSafeDemo;
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
            _lastCsvPath = descriptor.LastCsvPath;
            _view.RenderSettings(descriptor, SavedTokenPlaceholder);
            _view.RenderMessageValidation(_messageValidator.Validate(_view.MessageText));
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

            var result = await _csvImporter.ImportAsync(path, CancellationToken.None);
            if (result.Status != CsvImportStatus.Success)
            {
                if (result.Status != CsvImportStatus.Canceled)
                {
                    _view.ShowSafeError(result.SafeDiagnostic ?? "CSV import failed.");
                }

                return;
            }

            var savePath = await _settingsService.SaveLastCsvPathAsync(
                path,
                CancellationToken.None);
            if (savePath.Status != SaveCsvPathStatus.Saved)
            {
                var message = savePath.SafeMessage ??
                    "The CSV was read, but its path could not be saved.";
                _view.ShowSafeError(
                    $"{message} Existing contacts and the remembered CSV path were preserved.");
                return;
            }

            _lastCsvPath = path;
            ReplaceImportedContacts(result.Rows, selectedNumbers: null);
            _view.ShowSafeStatus($"Imported {_contacts.Count} contact(s); {_contacts.Count(row => row.IsEligible)} valid.");
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
            if (string.IsNullOrWhiteSpace(_lastCsvPath))
            {
                _view.ShowSafeError(
                    "No CSV file is remembered. Use Import CSV to choose and successfully import a file first.");
                return;
            }

            SynchronizeSelectionFromView();
            var selectedNumbers = _contacts
                .Where(row => row.IsEligible && _selection.Contains(row.ImportOrdinal))
                .Select(row => row.Number)
                .ToHashSet(StringComparer.Ordinal);
            var previousSelectedCount = selectedNumbers.Count;

            var result = await _csvImporter.ImportAsync(_lastCsvPath, CancellationToken.None);
            if (result.Status != CsvImportStatus.Success)
            {
                if (result.Status != CsvImportStatus.Canceled)
                {
                    _view.ShowSafeError(result.SafeDiagnostic ?? "CSV refresh failed.");
                }

                return;
            }

            ReplaceImportedContacts(result.Rows, selectedNumbers);
            var preservedCount = _selection.Count;
            var selectionStatus = previousSelectedCount == 0
                ? "Selection remains clear."
                : $"Preserved {preservedCount} of {previousSelectedCount} selected recipient(s) that remain eligible.";
            _view.ShowSafeStatus(
                $"Refreshed {_contacts.Count} contact(s); {_contacts.Count(row => row.IsEligible)} valid. {selectionStatus}");
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

    public void SelectAllEligible()
    {
        if (IsBatchActive || IsInteractionInProgress)
        {
            return;
        }

        _selection = _contacts.Where(row => row.IsEligible)
            .Select(row => row.ImportOrdinal)
            .ToHashSet();
        _view.ApplySelection(_selection);
        UpdateInteractionState();
    }

    public void ClearSelection()
    {
        if (IsBatchActive || IsInteractionInProgress)
        {
            return;
        }

        _selection.Clear();
        _view.ApplySelection(_selection);
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

        UpdateInteractionState();
        try
        {
            var message = _view.MessageText;
            SynchronizeSelectionFromView();
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
                    : "Import at least one valid contact before sending.");
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
            _selection = snapshot.Select(recipient => recipient.ImportOrdinal).ToHashSet();
            _view.ReplaceContacts(BuildRows(clearResults: true));
            _view.ApplySelection(_selection);
            _view.BeginBatch(batchId, scope, snapshot.Length, startedAt);
            UpdateInteractionState();

            var progress = new Progress<RecipientProgress>(_view.ApplyRecipientProgress);
            var run = await _batchCoordinator.TryRunAsync(
                request,
                progress,
                _batchCancellation.Token);
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
            _view.ShowSafeError("The send operation stopped because of an unexpected error. No automatic retry was attempted.");
        }
        finally
        {
            if (IsBatchActive)
            {
                IsBatchActive = false;
                _batchCancellation?.Dispose();
                _batchCancellation = null;
                _batchSettled.TrySetResult();
            }

            EndInteraction();
            UpdateInteractionState();
        }
    }

    public void CancelActiveBatch()
    {
        if (IsBatchActive)
        {
            _batchCancellation?.Cancel();
            _view.ShowSafeStatus("Cancellation requested. The in-flight recipient may still settle.");
        }
    }

    public async Task HandleActiveCloseRequestAsync()
    {
        if (!IsBatchActive || _closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        if (_dialogs.ConfirmCloseDuringBatch() == CloseDuringBatchChoice.Stay)
        {
            _closeRequestInProgress = false;
            return;
        }

        _closeWhenSettled = true;
        CancelActiveBatch();
        try
        {
            await _batchSettled.Task;
            if (_closeWhenSettled && !_closeBypassUsed)
            {
                _closeBypassUsed = true;
                _view.CloseWithBypass();
            }
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }

    private void SynchronizeSelectionFromView()
    {
        var eligible = _contacts.Where(row => row.IsEligible)
            .Select(row => row.ImportOrdinal)
            .ToHashSet();
        _selection = _view.SelectedOrdinals.Where(eligible.Contains).ToHashSet();
    }

    private RecipientSnapshot[] CreateSnapshot(SendScope scope) =>
        _contacts
            .Where(row => row.IsEligible &&
                (scope == SendScope.AllValid || _selection.Contains(row.ImportOrdinal)))
            .Select(row => new RecipientSnapshot(row.ImportOrdinal, row.Name, row.Number))
            .ToArray();

    private void ReplaceImportedContacts(
        IReadOnlyList<ContactRow> rows,
        IReadOnlySet<string>? selectedNumbers)
    {
        _contacts = rows.ToArray();
        _selection = selectedNumbers is null
            ? []
            : _contacts
                .Where(row => row.IsEligible && selectedNumbers.Contains(row.Number))
                .Select(row => row.ImportOrdinal)
                .ToHashSet();
        _view.ReplaceContacts(BuildRows(clearResults: true));
        _view.ApplySelection(_selection);
    }

    private IReadOnlyList<ContactGridRowViewModel> BuildRows(bool clearResults) =>
        _contacts.Select(row => new ContactGridRowViewModel(
                row.ImportOrdinal,
                _selection.Contains(row.ImportOrdinal),
                row.IsEligible,
                row.Name,
                row.Number,
                row.IsEligible ? "Valid" : "Invalid",
                clearResults ? null : RecipientSendState.Pending,
                null,
                null,
                row.Errors.Count == 0 ? null : string.Join(", ", row.Errors)))
            .ToArray();

    private void UpdateInteractionState()
    {
        var mutable = !IsBatchActive && !IsInteractionInProgress;
        var validCount = _contacts.Count(row => row.IsEligible);
        var selectedCount = _selection.Count;
        _view.SetInteractionState(new(
            IsBatchActive,
            CanEditSetup: mutable,
            CanSaveSetup: mutable,
            CanImport: mutable,
            CanRefresh: mutable,
            CanChangeSelection: mutable && validCount > 0,
            CanEditMessage: mutable,
            CanSendSelected: mutable && validCount > 0,
            CanSendAllValid: mutable && validCount > 0,
            CanCancel: IsBatchActive));
    }

    private bool IsInteractionInProgress => Volatile.Read(ref _interactionInProgress) != 0;

    private bool TryBeginInteraction() =>
        !IsBatchActive && Interlocked.CompareExchange(ref _interactionInProgress, 1, 0) == 0;

    private void EndInteraction() => Volatile.Write(ref _interactionInProgress, 0);

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
