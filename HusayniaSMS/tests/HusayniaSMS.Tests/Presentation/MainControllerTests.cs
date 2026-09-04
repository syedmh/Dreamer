using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.Tests.TestDoubles;
using HusayniaSMS.WinForms.Forms;
using HusayniaSMS.WinForms.Infrastructure.Csv;
using HusayniaSMS.WinForms.Presentation;
using System.Reflection;
using System.Windows.Forms;

namespace HusayniaSMS.Tests.Presentation;

[TestClass]
public sealed class MainControllerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task InitializeShowsIncompleteFirstRunAndMessageCount()
    {
        var view = new FakeMainView();
        var controller = Create(view: view);
        await controller.InitializeAsync();
        Assert.AreEqual(TokenState.Missing, view.Descriptor!.TokenState);
        Assert.AreEqual(TwilioSenderMode.FromPhoneNumber, view.Descriptor.SenderMode);
        Assert.AreEqual(string.Empty, view.Descriptor.SenderValue);
        Assert.AreEqual(MainController.SavedTokenPlaceholder, view.Placeholder);
        Assert.IsFalse(view.MessageValidation!.IsValid);
        StringAssert.Contains(view.Status!, "incomplete");
    }

    [TestMethod]
    public async Task InitializeRendersSavedMessagingServiceModeAndValue()
    {
        const string serviceSid = "MG0123456789abcdef0123456789ABCDEF";
        var view = new FakeMainView();
        var settings = new ReadySettingsService
        {
            SenderMode = TwilioSenderMode.MessagingServiceSid,
            SenderValue = serviceSid,
            DescriptorTokenState = TokenState.Available
        };

        await Create(view: view, settings: settings).InitializeAsync();

        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, view.Descriptor!.SenderMode);
        Assert.AreEqual(serviceSid, view.Descriptor.SenderValue);
        Assert.AreEqual(TokenState.Available, view.Descriptor.TokenState);
    }

    [TestMethod]
    public async Task SaveSetupForwardsSelectedModeAndValue()
    {
        const string serviceSid = "MG0123456789abcdef0123456789ABCDEF";
        var view = new FakeMainView
        {
            SetupInput = new(
                "AC0123456789abcdef0123456789ABCDEF",
                TwilioSenderMode.MessagingServiceSid,
                serviceSid,
                "token",
                false)
        };
        var settings = new ReadySettingsService();

        await Create(view: view, settings: settings).SaveSetupAsync();

        Assert.IsNotNull(settings.SavedSetupInput);
        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, settings.SavedSetupInput.SenderMode);
        Assert.AreEqual(serviceSid, settings.SavedSetupInput.SenderValue);
        Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, view.Descriptor!.SenderMode);
        Assert.AreEqual(serviceSid, view.Descriptor.SenderValue);
    }

    [TestMethod]
    public async Task CanceledChooserLeavesCurrentContactsAndSelectionUnchanged()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var settings = new ReadySettingsService();
        var importer = new StubImporter(new(CsvImportStatus.Success,
            new[] { ValidRow(1) }, null));
        var controller = Create(view, dialogs, importer, settings);
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();
        var rows = view.Rows;
        var selection = view.AppliedSelection.ToArray();

        dialogs.CsvPath = null;
        await controller.ImportContactsAsync();

        Assert.AreEqual(1, importer.CallCount);
        CollectionAssert.AreEqual(new[] { "contacts.csv" }, settings.SavedCsvPaths.ToArray());
        Assert.AreSame(rows, view.Rows);
        CollectionAssert.AreEquivalent(selection, view.AppliedSelection.ToArray());
    }

    [TestMethod]
    public async Task FailedBrowseDoesNotOverwriteRememberedSuccessfulPath()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "good.csv" };
        var settings = new ReadySettingsService();
        var importer = new SequenceImporter(
            new(CsvImportStatus.Success, new[] { ValidRow(1) }, null),
            new(CsvImportStatus.MalformedCsv, Array.Empty<ContactRow>(), "Malformed CSV."));
        var controller = Create(view, dialogs, importer, settings);

        await controller.ImportContactsAsync();
        var rows = view.Rows;
        dialogs.CsvPath = "bad.csv";
        await controller.ImportContactsAsync();

        CollectionAssert.AreEqual(new[] { "good.csv" }, settings.SavedCsvPaths.ToArray());
        Assert.AreSame(rows, view.Rows);
        StringAssert.Contains(view.Error!, "Malformed");
    }

    [TestMethod]
    public async Task SuccessfulCsvReadWithPathSaveFailurePreservesCurrentGridAndRememberedPath()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "new.csv" };
        var settings = new ReadySettingsService
        {
            LastCsvPath = "remembered.csv",
            SaveCsvPathResult = new(
                SaveCsvPathStatus.StorageFailed,
                "The CSV path could not be saved.")
        };
        view.ReplaceContacts(new[]
        {
            new ContactGridRowViewModel(
                4, true, true, "Existing", "+15550100104", "Valid",
                RecipientSendState.Succeeded, "SM-OLD", null, null)
        });
        var existingRows = view.Rows;
        var controller = Create(
            view,
            dialogs,
            new StubImporter(new(
                CsvImportStatus.Success,
                new[] { ValidRow(1) },
                null)),
            settings);

        await controller.ImportContactsAsync();

        Assert.AreSame(existingRows, view.Rows);
        Assert.AreEqual("remembered.csv", settings.LastCsvPath);
        Assert.AreEqual(0, settings.SavedCsvPaths.Count);
        StringAssert.Contains(view.Error!, "could not be saved");
    }

    [TestMethod]
    public async Task RefreshWithoutRememberedPathIsActionableAndNonDestructive()
    {
        var view = new FakeMainView();
        view.ReplaceContacts(new[]
        {
            new ContactGridRowViewModel(
                7, true, true, "Existing", "+15550100107", "Valid",
                RecipientSendState.Succeeded, "SM-OLD", null, null)
        });
        var existingRows = view.Rows;
        var importer = new StubImporter(new(
            CsvImportStatus.Success,
            new[] { ValidRow(1) },
            null));
        var controller = Create(view, importer: importer);

        await controller.RefreshContactsAsync();

        Assert.AreEqual(0, importer.CallCount);
        Assert.AreSame(existingRows, view.Rows);
        StringAssert.Contains(view.Error!, "Import CSV");
    }

    [TestMethod]
    public async Task RestartDescriptorLoadsRememberedPathForDialogFreeRefresh()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "different.csv" };
        var settings = new ReadySettingsService { LastCsvPath = "remembered.csv" };
        var importer = new SequenceImporter(
            new CsvImportResult(
                CsvImportStatus.Success,
                new[] { ValidRow(1) },
                null));
        var controller = Create(view, dialogs, importer, settings);

        await controller.InitializeAsync();
        await controller.RefreshContactsAsync();

        Assert.AreEqual("remembered.csv", view.Descriptor!.LastCsvPath);
        CollectionAssert.AreEqual(new[] { "remembered.csv" }, importer.Paths.ToArray());
        Assert.AreEqual(0, dialogs.CsvSelectionCount);
        Assert.AreEqual(1, view.Rows.Count);
    }

    [TestMethod]
    public async Task SuccessfulRefreshPreservesMatchingEligibleSelection()
    {
        var originalSelected = new ContactRow(
            1, "Selected person", "+15550100101", Array.Empty<ContactErrorCode>());
        var originalUnselected = new ContactRow(
            2, "Other person", "+15550100102", Array.Empty<ContactErrorCode>());
        var refreshedMatch = new ContactRow(
            10, "Renamed person", "+15550100101", Array.Empty<ContactErrorCode>());
        var refreshedOther = new ContactRow(
            11, "New person", "+15550100103", Array.Empty<ContactErrorCode>());
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var settings = new ReadySettingsService();
        var importer = new SequenceImporter(
            new(CsvImportStatus.Success, new[] { originalSelected, originalUnselected }, null),
            new(CsvImportStatus.Success, new[] { refreshedMatch, refreshedOther }, null));
        var controller = Create(view, dialogs, importer, settings);

        await controller.ImportContactsAsync();
        view.SelectedOrdinals = new[] { 1 };
        await controller.RefreshContactsAsync();

        CollectionAssert.AreEquivalent(new[] { 10 }, view.AppliedSelection.ToArray());
        Assert.IsTrue(view.Rows.Single(row => row.ImportOrdinal == 10).IsSelected);
        Assert.IsFalse(view.Rows.Single(row => row.ImportOrdinal == 11).IsSelected);
        CollectionAssert.AreEqual(
            new[] { "contacts.csv", "contacts.csv" },
            importer.Paths.ToArray());
        Assert.AreEqual(1, dialogs.CsvSelectionCount);
        StringAssert.Contains(view.Status!, "Preserved 1 of 1");
    }

    [TestMethod]
    [DataRow(CsvImportStatus.FileNotFound, "CSV file was not found.")]
    [DataRow(CsvImportStatus.AccessDenied, "Access to the CSV file was denied.")]
    [DataRow(CsvImportStatus.MalformedCsv, "CSV is not valid UTF-8 text.")]
    [DataRow(CsvImportStatus.InconsistentRecord, "CSV exceeds the file size limit.")]
    [DataRow(CsvImportStatus.IoFailure, "CSV file could not be read.")]
    public async Task FailedRefreshPreservesContactsSelectionResultsAndRememberedPath(
        CsvImportStatus failureStatus,
        string diagnostic)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var settings = new ReadySettingsService();
        var importer = new SequenceImporter(
            new(CsvImportStatus.Success, new[]
            {
                ValidRow(1),
                new ContactRow(
                    2,
                    "Invalid",
                    "not-a-number",
                    new[] { ContactErrorCode.InvalidE164 })
            }, null),
            new(failureStatus, Array.Empty<ContactRow>(), diagnostic));
        var controller = Create(view, dialogs, importer, settings);
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();
        view.SelectedOrdinals = new[] { 1 };
        view.ApplyRecipientProgress(new(
            Guid.NewGuid(),
            1,
            RecipientSendState.Succeeded,
            "SM-EXISTING",
            null,
            null));
        var rows = view.Rows;
        var selection = view.AppliedSelection.ToArray();

        await controller.RefreshContactsAsync();

        Assert.AreSame(rows, view.Rows);
        Assert.AreEqual(
            "SM-EXISTING",
            view.Rows.Single(row => row.ImportOrdinal == 1).ProviderMessageId);
        CollectionAssert.AreEquivalent(selection, view.AppliedSelection.ToArray());
        CollectionAssert.AreEqual(new[] { "contacts.csv" }, settings.SavedCsvPaths.ToArray());
        Assert.AreEqual(1, dialogs.CsvSelectionCount);
        Assert.AreEqual(diagnostic, view.Error);
    }

    [TestMethod]
    public async Task OversizedCsvRejectionPreservesPopulatedContactsAndSelection()
    {
        using var temp = new TempDirectory();
        var validPath = temp.File("valid.csv");
        await File.WriteAllTextAsync(validPath,
            "Name,Number\r\nAlice,+15550100100\r\n");
        var oversizedPath = temp.File("oversized.csv");
        await using (var stream = new FileStream(
            oversizedPath, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(CsvHelperContactCsvImporter.MaximumFileBytes + 1L);
        }

        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = validPath };
        var phone = new E164PhoneNumberValidator();
        var importer = new CsvHelperContactCsvImporter(new ContactRowValidator(phone));
        var controller = Create(view, dialogs, importer);
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();
        var rows = view.Rows;
        var selection = view.AppliedSelection.ToArray();

        dialogs.CsvPath = oversizedPath;
        await controller.ImportContactsAsync();

        Assert.AreSame(rows, view.Rows);
        CollectionAssert.AreEquivalent(selection, view.AppliedSelection.ToArray());
        StringAssert.Contains(view.Error!, "file size");
    }

    [TestMethod]
    public async Task SuccessfulImportSelectAllAndClearOnlyAffectEligibleRows()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var importer = new StubImporter(new(CsvImportStatus.Success,
            new[]
            {
                ValidRow(1),
                new ContactRow(2, "Bad", "x", new[] { ContactErrorCode.InvalidE164 })
            }, null));
        var controller = Create(view, dialogs, importer);
        await controller.ImportContactsAsync();

        Assert.AreEqual(2, view.Rows.Count);
        Assert.IsFalse(view.Rows[1].CanSelect);
        Assert.IsTrue(view.InteractionState!.CanSendSelected);
        controller.SelectAllEligible();
        CollectionAssert.AreEquivalent(new[] { 1 }, view.AppliedSelection.ToArray());
        controller.ClearSelection();
        Assert.AreEqual(0, view.AppliedSelection.Count);
    }

    [TestMethod]
    public async Task InvalidMessageRejectsBeforeConfirmationAndCoordinator()
    {
        var view = new FakeMainView { MessageText = "   ", SelectedOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        await controller.SendAsync(SendScope.Selected);

        Assert.AreEqual(0, dialogs.SendConfirmationCount);
        Assert.AreEqual(0, coordinator.CallCount);
        StringAssert.Contains(view.Error!, "1,600");
    }

    [TestMethod]
    public async Task IncompleteSetupRejectsBeforeConfirmationAndCoordinator()
    {
        var view = new FakeMainView { MessageText = "hello", SelectedOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            new IncompleteSettingsService(),
            coordinator);
        await controller.ImportContactsAsync();
        await controller.SendAsync(SendScope.Selected);
        Assert.AreEqual(0, dialogs.SendConfirmationCount);
        Assert.AreEqual(0, coordinator.CallCount);
        StringAssert.Contains(view.Error!, "Complete");
    }

    [TestMethod]
    public async Task SelectedAndAllValidScopesUseExplicitImmutableCounts()
    {
        var view = new FakeMainView { MessageText = "hello" };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv", ConfirmSendResult = false };
        var rows = new[] { ValidRow(1), ValidRow(2) };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, rows, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();

        view.SelectedOrdinals = new[] { 1 };
        await controller.SendAsync(SendScope.Selected);
        Assert.AreEqual((SendScope.Selected, 1, true), dialogs.LastConfirmation);
        Assert.AreEqual(0, coordinator.CallCount);

        await controller.SendAsync(SendScope.AllValid);
        Assert.AreEqual((SendScope.AllValid, 2, true), dialogs.LastConfirmation);
        Assert.AreEqual(0, coordinator.CallCount);
    }

    [TestMethod]
    public async Task SelectedScopeWithNoEligibleSelectionDoesNotConfirmOrSend()
    {
        var view = new FakeMainView { MessageText = "hello" };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();

        view.SelectedOrdinals = Array.Empty<int>();
        await controller.SendAsync(SendScope.Selected);

        Assert.AreEqual(0, dialogs.SendConfirmationCount);
        Assert.AreEqual(0, coordinator.CallCount);
        StringAssert.Contains(view.Error!, "Select at least one");
    }

    [TestMethod]
    public async Task DelayedCredentialLoadUsesCapturedMessageAndRejectsDuplicatePreflight()
    {
        var view = new FakeMainView
        {
            MessageText = "original validated message",
            SelectedOrdinals = new[] { 1 }
        };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var settings = new ControllableSettingsService(delayCredentials: true);
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            settings,
            coordinator);
        await controller.ImportContactsAsync();

        var first = controller.SendAsync(SendScope.Selected);
        await settings.CredentialsStarted.Task;
        Assert.IsFalse(view.InteractionState!.CanEditSetup);
        Assert.IsFalse(view.InteractionState.CanImport);
        Assert.IsFalse(view.InteractionState.CanRefresh);
        Assert.IsFalse(view.InteractionState.CanEditMessage);
        view.MessageText = "mutated while credentials were loading";

        await controller.SendAsync(SendScope.Selected);
        Assert.AreEqual(1, settings.CredentialLoadCount);
        Assert.AreEqual(0, dialogs.SendConfirmationCount);
        Assert.AreEqual(0, coordinator.CallCount);

        settings.ReleaseCredentials();
        await first;

        Assert.AreEqual(1, dialogs.SendConfirmationCount);
        Assert.AreEqual(1, coordinator.CallCount);
        Assert.AreEqual("original validated message", coordinator.LastRequest!.Message);
    }

    [TestMethod]
    public async Task DelayedInitializationOwnsInteractionGateAndRejectsOverlap()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var importer = new StubImporter(new(CsvImportStatus.Success,
            new[] { ValidRow(1) }, null));
        var settings = new ControllableSettingsService(delayDescriptor: true);
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs, importer, settings, coordinator);

        var initialize = controller.InitializeAsync();
        await settings.DescriptorStarted.Task;
        Assert.IsNotNull(view.InteractionState);
        Assert.IsFalse(view.InteractionState.CanEditSetup);
        Assert.IsFalse(view.InteractionState.CanImport);
        Assert.IsFalse(view.InteractionState.CanRefresh);

        var duplicateInitialize = controller.InitializeAsync();
        var save = controller.SaveSetupAsync();
        var import = controller.ImportContactsAsync();
        var refresh = controller.RefreshContactsAsync();
        var send = controller.SendAsync(SendScope.AllValid);
        settings.ReleaseDescriptor();
        await Task.WhenAll(initialize, duplicateInitialize, save, import, refresh, send);

        Assert.AreEqual(1, settings.DescriptorLoadCount);
        Assert.AreEqual(0, settings.SaveCount);
        Assert.AreEqual(0, settings.CredentialLoadCount);
        Assert.AreEqual(0, importer.CallCount);
        Assert.AreEqual(0, coordinator.CallCount);
        Assert.IsTrue(view.InteractionState!.CanEditSetup);
        Assert.IsTrue(view.InteractionState.CanImport);
        Assert.IsTrue(view.InteractionState.CanRefresh);
        Assert.IsTrue(view.InteractionState.CanEditMessage);
    }

    [TestMethod]
    public async Task DelayedRefreshRejectsDuplicateRefreshAndImportWithoutQueuing()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = "chooser.csv" };
        var settings = new ReadySettingsService { LastCsvPath = "remembered.csv" };
        var importer = new ControllableImporter(
            new(CsvImportStatus.Success, new[] { ValidRow(1) }, null));
        var controller = Create(view, dialogs, importer, settings);
        await controller.InitializeAsync();

        var first = controller.RefreshContactsAsync();
        await importer.Started.Task;
        Assert.IsFalse(view.InteractionState!.CanImport);
        Assert.IsFalse(view.InteractionState.CanRefresh);

        await controller.RefreshContactsAsync();
        await controller.ImportContactsAsync();
        Assert.AreEqual(1, importer.CallCount);
        Assert.AreEqual(0, dialogs.CsvSelectionCount);

        importer.Release();
        await first;
        Assert.IsTrue(view.InteractionState!.CanImport);
        Assert.IsTrue(view.InteractionState.CanRefresh);
    }

    [TestMethod]
    public async Task ActiveBatchDisablesMutationRejectsDoubleSubmitAndRestoresControls()
    {
        var view = new FakeMainView { MessageText = "hello", SelectedOrdinals = new[] { 1, 2 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success,
                new[] { ValidRow(1), ValidRow(2) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();

        var first = controller.SendAsync(SendScope.Selected);
        await coordinator.Started.Task;
        Assert.IsTrue(controller.IsBatchActive);
        Assert.IsFalse(view.InteractionState!.CanImport);
        Assert.IsFalse(view.InteractionState.CanRefresh);
        Assert.IsFalse(view.InteractionState.CanEditMessage);
        await controller.SendAsync(SendScope.Selected);
        await controller.RefreshContactsAsync();
        Assert.AreEqual(1, coordinator.CallCount);

        coordinator.Complete();
        await first;
        Assert.IsFalse(controller.IsBatchActive);
        Assert.IsTrue(view.InteractionState!.CanImport);
        Assert.IsTrue(view.InteractionState.CanRefresh);
        Assert.AreEqual(2, coordinator.LastRequest!.Recipients.Count);
        Assert.AreEqual(Now, view.BeganBatch!.Value.StartedAt);
    }

    [TestMethod]
    public async Task CancellationAndCancelCloseAwaitSettlementAndBypassOnce()
    {
        var view = new FakeMainView { MessageText = "hello", SelectedOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            CloseChoice = CloseDuringBatchChoice.CancelRemainingAndClose
        };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        var send = controller.SendAsync(SendScope.Selected);
        await coordinator.Started.Task;
        var close = controller.HandleActiveCloseRequestAsync();
        Assert.IsTrue(coordinator.LastToken.IsCancellationRequested);
        coordinator.Complete(canceled: true);
        await Task.WhenAll(send, close);
        Assert.AreEqual(1, view.CloseBypassCount);
    }

    [TestMethod]
    public async Task CloseStayLeavesBatchRunningAndRepeatedCancelClosePromptsOnlyOnce()
    {
        var view = new FakeMainView { MessageText = "hello", SelectedOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            CloseChoice = CloseDuringBatchChoice.Stay
        };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        var send = controller.SendAsync(SendScope.Selected);
        await coordinator.Started.Task;

        await controller.HandleActiveCloseRequestAsync();
        Assert.IsTrue(controller.IsBatchActive);
        Assert.IsFalse(coordinator.LastToken.IsCancellationRequested);
        Assert.AreEqual(0, view.CloseBypassCount);

        dialogs.CloseChoice = CloseDuringBatchChoice.CancelRemainingAndClose;
        var firstClose = controller.HandleActiveCloseRequestAsync();
        var repeatedClose = controller.HandleActiveCloseRequestAsync();
        Assert.IsTrue(coordinator.LastToken.IsCancellationRequested);
        Assert.AreEqual(2, dialogs.CloseConfirmationCount);
        coordinator.Complete();
        await Task.WhenAll(send, firstClose, repeatedClose);
        Assert.AreEqual(1, view.CloseBypassCount);
    }

    [TestMethod]
    public void FailureFormattingKeepsActionableGuidanceWithSafeCodeAsContext()
    {
        foreach (var (code, message) in new[]
        {
            ("TWILIO_21211", "Correct the recipient number before retrying."),
            ("NETWORK_UNKNOWN", "Verify provider status before retrying this recipient."),
            ("HTTP_401", "Review Twilio account and sender setup before retrying.")
        })
        {
            var formatted = FormatResult(new ContactGridRowViewModel(
                1, true, true, "One", "+15550100100", "Valid",
                RecipientSendState.Failed, null, code, message));

            StringAssert.Contains(formatted, message);
            StringAssert.Contains(formatted, code);
            Assert.IsTrue(formatted.IndexOf(message, StringComparison.Ordinal) <
                formatted.IndexOf(code, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task MainFormStaStartupPreservesPlaceholderAndMarshalsProgressToUiThread()
    {
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var settings = new ControllableSettingsService(delayCredentials: true);
        var coordinator = new BackgroundProgressCoordinator();
        await RunMainFormAsync(
            form => new MainController(
                form,
                dialogs,
                new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
                settings,
                new MessageValidator(),
                coordinator,
                new FakeTimeProvider(Now),
                isSafeDemo: true),
            async (form, controller) =>
        {
            var grid = GetField<DataGridView>(form, "contactsGrid");
            var resultColumn = GetField<DataGridViewTextBoxColumn>(form, "resultColumn");
            var uiThreadId = Environment.CurrentManagedThreadId;
            var progressThreadId = 0;
            grid.CellValueChanged += (_, args) =>
            {
                if (args.ColumnIndex == resultColumn.Index)
                {
                    progressThreadId = Environment.CurrentManagedThreadId;
                }
            };

            await WaitUntilAsync(() =>
                GetField<Label>(form, "applicationStatusLabel").Text.Contains(
                    "incomplete", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(GetField<Label>(form, "setupStatusLabel").Text, "incomplete");

            var descriptor = new SettingsDescriptor(
                "AC0123456789abcdef0123456789ABCDEF",
                TwilioSenderMode.FromPhoneNumber,
                "+15550100999",
                TokenState.Available);
            form.RenderSettings(descriptor, MainController.SavedTokenPlaceholder);
            var setupInput = form.ReadSetupInput();
            Assert.IsTrue(setupInput.PreserveSavedToken);
            Assert.IsNull(setupInput.NewAuthToken);

            await controller.ImportContactsAsync();
            var messageTextBox = GetField<TextBox>(form, "messageTextBox");
            messageTextBox.Text = "hello from STA";
            controller.SelectAllEligible();
            var send = controller.SendAsync(SendScope.Selected);
            await settings.CredentialsStarted.Task;
            Assert.IsFalse(messageTextBox.Enabled);
            Assert.IsFalse(GetField<ComboBox>(form, "senderModeComboBox").Enabled);
            Assert.IsFalse(GetField<TextBox>(form, "senderValueTextBox").Enabled);
            messageTextBox.Text = "mutated during preflight";
            settings.ReleaseCredentials();
            await send;

            Assert.AreNotEqual(uiThreadId, coordinator.ReportThreadId);
            Assert.AreEqual(uiThreadId, progressThreadId);
            Assert.AreEqual("hello from STA", coordinator.Message);
            StringAssert.Contains(
                Convert.ToString(grid.Rows[0].Cells[resultColumn.Index].Value)!,
                "SM-UI");
        });
    }

    [TestMethod]
    public async Task MainFormSenderModeSelectorDefaultsMapsLabelsAndRestoresMessagingService()
    {
        const string serviceSid = "MG0123456789abcdef0123456789ABCDEF";
        await RunMainFormAsync(
            form => new MainController(
                form,
                new FakeUserDialogs(),
                new StubImporter(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
                new ReadySettingsService(),
                new MessageValidator(),
                new RecordingCoordinator(),
                new FakeTimeProvider(Now),
                isSafeDemo: true),
            (form, _) =>
            {
                var mode = GetField<ComboBox>(form, "senderModeComboBox");
                var label = GetField<Label>(form, "senderValueLabel");
                var value = GetField<TextBox>(form, "senderValueTextBox");

                Assert.AreEqual(ComboBoxStyle.DropDownList, mode.DropDownStyle);
                CollectionAssert.AreEqual(
                    new[] { "From phone number", "Messaging Service SID" },
                    mode.Items.Cast<string>().ToArray());
                Assert.AreEqual(0, mode.SelectedIndex);
                Assert.AreEqual("From phone number (E.164)", label.Text);

                value.Text = "+15550100999";
                mode.SelectedIndex = 1;
                Assert.AreEqual("+15550100999", value.Text);
                Assert.AreEqual("Messaging Service SID (MG...)", label.Text);
                Assert.AreEqual(
                    TwilioSenderMode.MessagingServiceSid,
                    form.ReadSetupInput().SenderMode);

                form.RenderSettings(new(
                    "AC0123456789abcdef0123456789ABCDEF",
                    TwilioSenderMode.MessagingServiceSid,
                    serviceSid,
                    TokenState.Available),
                    MainController.SavedTokenPlaceholder);
                var restored = form.ReadSetupInput();
                Assert.AreEqual(TwilioSenderMode.MessagingServiceSid, restored.SenderMode);
                Assert.AreEqual(serviceSid, restored.SenderValue);
                Assert.AreEqual("Messaging Service SID (MG...)", label.Text);
                var status = GetField<Label>(form, "setupStatusLabel").Text;
                StringAssert.Contains(status, "Messaging Service SID");
                Assert.IsFalse(status.Contains(serviceSid, StringComparison.Ordinal));
                Assert.IsFalse(status.Contains("••••", StringComparison.Ordinal));
                return Task.CompletedTask;
            });
    }

    [TestMethod]
    public async Task MainFormExposesRefreshControlBesideImport()
    {
        await RunMainFormAsync(
            form => new MainController(
                form,
                new FakeUserDialogs(),
                new StubImporter(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
                new ReadySettingsService(),
                new MessageValidator(),
                new RecordingCoordinator(),
                new FakeTimeProvider(Now),
                isSafeDemo: true),
            (form, _) =>
            {
                var import = GetField<Button>(form, "importButton");
                var refresh = GetField<Button>(form, "refreshButton");
                Assert.AreEqual("Refresh", refresh.Text);
                Assert.AreSame(import.Parent, refresh.Parent);
                Assert.IsTrue(refresh.Visible);
                return Task.CompletedTask;
            });
    }

    [TestMethod]
    public async Task MainFormButtonsUseContentSafeSizingWithoutClipping()
    {
        var buttonFieldNames = new[]
        {
            "saveSetupButton",
            "importButton",
            "refreshButton",
            "selectAllButton",
            "clearSelectionButton",
            "sendSelectedButton",
            "sendAllButton",
            "cancelButton"
        };

        await RunMainFormAsync(
            form => new MainController(
                form,
                new FakeUserDialogs(),
                new StubImporter(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
                new ReadySettingsService(),
                new MessageValidator(),
                new RecordingCoordinator(),
                new FakeTimeProvider(Now),
                isSafeDemo: true),
            (form, _) =>
            {
                form.PerformLayout();

                foreach (var fieldName in buttonFieldNames)
                {
                    var button = GetField<Button>(form, fieldName);
                    var renderedText = TextRenderer.MeasureText(
                        button.Text,
                        button.Font,
                        Size.Empty,
                        TextFormatFlags.SingleLine);
                    var preferredSize = button.GetPreferredSize(Size.Empty);

                    Assert.IsTrue(button.AutoSize, $"{fieldName} must size to its content.");
                    Assert.AreEqual(
                        AutoSizeMode.GrowAndShrink,
                        button.AutoSizeMode,
                        $"{fieldName} must grow horizontally for longer labels.");
                    Assert.AreEqual(
                        36,
                        button.MinimumSize.Height,
                        $"{fieldName} must use the common minimum visual height.");
                    Assert.AreEqual(
                        new Padding(12, 6, 12, 6),
                        button.Padding,
                        $"{fieldName} must use the common content padding.");
                    Assert.IsTrue(
                        preferredSize.Width >= renderedText.Width + button.Padding.Horizontal,
                        $"{fieldName} preferred width does not fit its text and padding.");
                    Assert.IsTrue(
                        preferredSize.Height >= renderedText.Height + button.Padding.Vertical,
                        $"{fieldName} preferred height does not fit its text and padding.");
                    Assert.IsTrue(
                        button.ClientSize.Width >= renderedText.Width,
                        $"{fieldName} client width clips its rendered text.");
                    Assert.IsTrue(
                        button.ClientSize.Height >= renderedText.Height,
                        $"{fieldName} client height clips its rendered text.");
                    Assert.IsTrue(
                        button.Width >= preferredSize.Width,
                        $"{fieldName} actual width is smaller than its preferred width.");
                    Assert.IsTrue(
                        button.Height >= preferredSize.Height,
                        $"{fieldName} actual height is smaller than its preferred height.");
                }

                return Task.CompletedTask;
            });
    }

    [TestMethod]
    public async Task CorrectedFollowUpBatchCanRunWithNewBatchId()
    {
        var view = new FakeMainView { MessageText = "first", SelectedOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubImporter(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        await controller.SendAsync(SendScope.Selected);
        var firstId = coordinator.LastRequest!.BatchId;
        view.MessageText = "corrected";
        await controller.SendAsync(SendScope.Selected);
        Assert.AreEqual(2, coordinator.CallCount);
        Assert.AreNotEqual(firstId, coordinator.LastRequest!.BatchId);
        Assert.AreEqual("corrected", coordinator.LastRequest.Message);
    }

    private static MainController Create(
        FakeMainView? view = null,
        FakeUserDialogs? dialogs = null,
        IContactCsvImporter? importer = null,
        ISettingsService? settings = null,
        IBatchSendCoordinator? coordinator = null) =>
        new(view ?? new FakeMainView(),
            dialogs ?? new FakeUserDialogs(),
            importer ?? new StubImporter(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
            settings ?? new ReadySettingsService(),
            new MessageValidator(),
            coordinator ?? new RecordingCoordinator(),
            new FakeTimeProvider(Now),
            isSafeDemo: true);

    private static ContactRow ValidRow(int ordinal) =>
        new(ordinal, $"Person {ordinal}", $"+1555010010{ordinal}", Array.Empty<ContactErrorCode>());

    private static string FormatResult(ContactGridRowViewModel item)
    {
        var method = typeof(MainForm).GetMethod(
            "FormatResult",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)method.Invoke(null, new object[] { item })!;
    }

    private static T GetField<T>(MainForm form, string name) where T : class
    {
        var field = typeof(MainForm).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(form)!;
    }

    private static async Task RunMainFormAsync(
        Func<MainForm, MainController> createController,
        Func<MainForm, MainController, Task> test)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new MainForm(isSafeDemo: true);
                var controller = createController(form);
                form.AttachController(controller);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        await test(form, controller);
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        form.Close();
                    }
                };
                Application.Run(form);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completion.TrySetResult();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join();
        if (failure is not null)
        {
            throw new AssertFailedException("STA WinForms harness failed.", failure);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class StubImporter(CsvImportResult result) : IContactCsvImporter
    {
        public int CallCount { get; private set; }
        public Task<CsvImportResult> ImportAsync(string path, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class SequenceImporter(params CsvImportResult[] results) : IContactCsvImporter
    {
        private readonly Queue<CsvImportResult> _results = new(results);
        public List<string> Paths { get; } = [];

        public Task<CsvImportResult> ImportAsync(
            string path,
            CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ControllableImporter(CsvImportResult result) : IContactCsvImporter
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CsvImportResult> ImportAsync(
            string path,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await _release.Task;
            return result;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ReadySettingsService : ISettingsService
    {
        public string? LastCsvPath { get; set; }
        public TwilioSenderMode SenderMode { get; set; } = TwilioSenderMode.FromPhoneNumber;
        public string SenderValue { get; set; } = string.Empty;
        public TokenState DescriptorTokenState { get; set; } = TokenState.Missing;
        public SetupInput? SavedSetupInput { get; private set; }
        public SaveCsvPathResult SaveCsvPathResult { get; set; } =
            new(SaveCsvPathStatus.Saved, "saved");
        public List<string> SavedCsvPaths { get; } = [];

        public Task<SettingsDescriptor> LoadDescriptorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsDescriptor(
                "",
                SenderMode,
                SenderValue,
                DescriptorTokenState,
                LastCsvPath));
        public Task<SaveSettingsResult> SaveAsync(
            SetupInput input,
            CancellationToken cancellationToken)
        {
            SavedSetupInput = input;
            SenderMode = input.SenderMode;
            SenderValue = input.SenderValue;
            DescriptorTokenState = TokenState.Available;
            return
            Task.FromResult(new SaveSettingsResult(
                SaveSettingsStatus.Saved, Array.Empty<FieldError>(), "saved"));
        }
        public Task<SaveCsvPathResult> SaveLastCsvPathAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (SaveCsvPathResult.Status == SaveCsvPathStatus.Saved)
            {
                LastCsvPath = path;
                SavedCsvPaths.Add(path);
            }

            return Task.FromResult(SaveCsvPathResult);
        }
        public Task<CredentialLoadResult> LoadCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialLoadResult(
                CredentialLoadStatus.Ready,
                new TwilioCredentials(
                    "AC0123456789abcdef0123456789ABCDEF",
                    SenderMode,
                    string.IsNullOrEmpty(SenderValue) ? "+15550100999" : SenderValue,
                    "test-token"),
                Array.Empty<FieldError>(), null));
    }

    private sealed class IncompleteSettingsService : ISettingsService
    {
        public Task<SettingsDescriptor> LoadDescriptorAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsDescriptor(
                "",
                TwilioSenderMode.FromPhoneNumber,
                "",
                TokenState.Missing));
        public Task<SaveSettingsResult> SaveAsync(SetupInput input, CancellationToken cancellationToken) =>
            Task.FromResult(new SaveSettingsResult(
                SaveSettingsStatus.ValidationFailed,
                new[] { new FieldError("AuthToken", "Required", "Auth token is required.") },
                null));
        public Task<SaveCsvPathResult> SaveLastCsvPathAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SaveCsvPathResult(SaveCsvPathStatus.Saved, "saved"));
        public Task<CredentialLoadResult> LoadCredentialsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialLoadResult(
                CredentialLoadStatus.Incomplete,
                null,
                new[] { new FieldError("AuthToken", "Required", "Auth token is required.") },
                "Complete Twilio setup before sending."));
    }

    private sealed class ControllableSettingsService(
        bool delayDescriptor = false,
        bool delayCredentials = false) : ISettingsService
    {
        private readonly TaskCompletionSource _descriptorRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _credentialsRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DescriptorLoadCount { get; private set; }
        public int CredentialLoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public int CsvPathSaveCount { get; private set; }
        public TaskCompletionSource DescriptorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CredentialsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SettingsDescriptor> LoadDescriptorAsync(
            CancellationToken cancellationToken)
        {
            DescriptorLoadCount++;
            DescriptorStarted.TrySetResult();
            if (delayDescriptor)
            {
                await _descriptorRelease.Task;
            }

            return new(
                "",
                TwilioSenderMode.FromPhoneNumber,
                "",
                TokenState.Missing);
        }

        public Task<SaveSettingsResult> SaveAsync(
            SetupInput input,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(new SaveSettingsResult(
                SaveSettingsStatus.Saved,
                Array.Empty<FieldError>(),
                "saved"));
        }

        public Task<SaveCsvPathResult> SaveLastCsvPathAsync(
            string path,
            CancellationToken cancellationToken)
        {
            CsvPathSaveCount++;
            return Task.FromResult(new SaveCsvPathResult(
                SaveCsvPathStatus.Saved,
                "saved"));
        }

        public async Task<CredentialLoadResult> LoadCredentialsAsync(
            CancellationToken cancellationToken)
        {
            CredentialLoadCount++;
            CredentialsStarted.TrySetResult();
            if (delayCredentials)
            {
                await _credentialsRelease.Task;
            }

            return new(
                CredentialLoadStatus.Ready,
                new TwilioCredentials(
                    "AC0123456789abcdef0123456789ABCDEF",
                    TwilioSenderMode.FromPhoneNumber,
                    "+15550100999",
                    "test-token"),
                Array.Empty<FieldError>(),
                null);
        }

        public void ReleaseDescriptor() => _descriptorRelease.TrySetResult();
        public void ReleaseCredentials() => _credentialsRelease.TrySetResult();
    }

    private sealed class BackgroundProgressCoordinator : IBatchSendCoordinator
    {
        public int ReportThreadId { get; private set; }
        public string? Message { get; private set; }

        public async Task<BatchRunResult> TryRunAsync(
            SmsBatchRequest request,
            IProgress<RecipientProgress> progress,
            CancellationToken cancellationToken)
        {
            Message = request.Message;
            await Task.Run(async () =>
            {
                await Task.Delay(25, cancellationToken);
                ReportThreadId = Environment.CurrentManagedThreadId;
                progress.Report(new(
                    request.BatchId,
                    request.Recipients[0].ImportOrdinal,
                    RecipientSendState.Succeeded,
                    "SM-UI",
                    null,
                    null));
            }, cancellationToken);

            return new(
                BatchStartStatus.Completed,
                new BatchSummary(request.BatchId, 1, 1, 0, 0));
        }
    }

    private sealed class RecordingCoordinator : IBatchSendCoordinator
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public SmsBatchRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public bool Delay { get; set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BatchRunResult> TryRunAsync(
            SmsBatchRequest request,
            IProgress<RecipientProgress> progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastToken = cancellationToken;
            Started.TrySetResult();
            if (Delay)
            {
                await _completion.Task;
            }

            var canceled = cancellationToken.IsCancellationRequested ? request.Recipients.Count : 0;
            var succeeded = request.Recipients.Count - canceled;
            return new(canceled > 0 ? BatchStartStatus.Canceled : BatchStartStatus.Completed,
                new BatchSummary(request.BatchId, request.Recipients.Count, succeeded, 0, canceled));
        }

        public void Complete(bool canceled = false) => _completion.TrySetResult();
    }
}
