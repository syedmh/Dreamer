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
        var importer = new StubContactCsvStore(new(CsvImportStatus.Success,
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
        var importer = new SequenceContactCsvStore(
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
        var existingGridRows = new[]
        {
            new ContactGridRowViewModel(
                4, true, true, "Existing", "+15550100104", "Valid",
                RecipientSendState.Succeeded, "SM-OLD", null, null)
        };
        view.RenderDocumentState(new(
            "remembered.csv",
            false,
            existingGridRows,
            new HashSet<int> { 4 },
            Array.Empty<int>().ToHashSet()));
        var existingRows = view.Rows;
        var controller = Create(
            view,
            dialogs,
            new StubContactCsvStore(new(
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
        var existingGridRows = new[]
        {
            new ContactGridRowViewModel(
                7, true, true, "Existing", "+15550100107", "Valid",
                RecipientSendState.Succeeded, "SM-OLD", null, null)
        };
        view.RenderDocumentState(new(
            "remembered.csv",
            false,
            existingGridRows,
            new HashSet<int> { 7 },
            Array.Empty<int>().ToHashSet()));
        var existingRows = view.Rows;
        var importer = new StubContactCsvStore(new(
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
        var importer = new SequenceContactCsvStore(
            new TestLoadResult(
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
        var importer = new SequenceContactCsvStore(
            new(CsvImportStatus.Success, new[] { originalSelected, originalUnselected }, null),
            new(CsvImportStatus.Success, new[] { refreshedMatch, refreshedOther }, null));
        var controller = Create(view, dialogs, importer, settings);

        await controller.ImportContactsAsync();
        view.CheckedRecipientOrdinals = new[] { 1 };
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
        var importer = new SequenceContactCsvStore(
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
        view.CheckedRecipientOrdinals = new[] { 1 };
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
            stream.SetLength(CsvHelperContactCsvStore.MaximumFileBytes + 1L);
        }

        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { CsvPath = validPath };
        var phone = new E164PhoneNumberValidator();
        var importer = new CsvHelperContactCsvStore(new ContactRowValidator(phone));
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
        var importer = new StubContactCsvStore(new(CsvImportStatus.Success,
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
        var view = new FakeMainView { MessageText = "   ", CheckedRecipientOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
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
        var view = new FakeMainView { MessageText = "hello", CheckedRecipientOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
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
            new StubContactCsvStore(new(CsvImportStatus.Success, rows, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();

        view.CheckedRecipientOrdinals = new[] { 1 };
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
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();

        view.CheckedRecipientOrdinals = Array.Empty<int>();
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
            CheckedRecipientOrdinals = new[] { 1 }
        };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var settings = new ControllableSettingsService(delayCredentials: true);
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            settings,
            coordinator);
        await controller.ImportContactsAsync();
        view.CheckedRecipientOrdinals = new[] { 1 };

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
        var importer = new StubContactCsvStore(new(CsvImportStatus.Success,
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
        var importer = new ControllableContactCsvStore(
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
        var view = new FakeMainView { MessageText = "hello", CheckedRecipientOrdinals = new[] { 1, 2 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success,
                new[] { ValidRow(1), ValidRow(2) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        view.CheckedRecipientOrdinals = new[] { 1, 2 };

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
        var view = new FakeMainView { MessageText = "hello", CheckedRecipientOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            CloseChoice = CloseDuringBatchChoice.CancelRemainingAndClose
        };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        view.CheckedRecipientOrdinals = new[] { 1 };
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
        var view = new FakeMainView { MessageText = "hello", CheckedRecipientOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            CloseChoice = CloseDuringBatchChoice.Stay
        };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        view.CheckedRecipientOrdinals = new[] { 1 };
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
                new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
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
                new StubContactCsvStore(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
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
                new StubContactCsvStore(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
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
            "addContactButton",
            "editContactButton",
            "deleteSelectedButton",
            "saveCsvButton",
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
                new StubContactCsvStore(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
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
        var view = new FakeMainView { MessageText = "first", CheckedRecipientOrdinals = new[] { 1 } };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var coordinator = new RecordingCoordinator();
        var controller = Create(view, dialogs,
            new StubContactCsvStore(new(CsvImportStatus.Success, new[] { ValidRow(1) }, null)),
            coordinator: coordinator);
        await controller.ImportContactsAsync();
        view.CheckedRecipientOrdinals = new[] { 1 };
        await controller.SendAsync(SendScope.Selected);
        var firstId = coordinator.LastRequest!.BatchId;
        view.MessageText = "corrected";
        await controller.SendAsync(SendScope.Selected);
        Assert.AreEqual(2, coordinator.CallCount);
        Assert.AreNotEqual(firstId, coordinator.LastRequest!.BatchId);
        Assert.AreEqual("corrected", coordinator.LastRequest.Message);
    }

    [TestMethod]
    public void AddBeforeLoadAppendsUncheckedHighlightsAndMarksDirty()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new(" New person ", " +15550100100 ")
        };
        var controller = CreateWithStore(view, dialogs, new FakeContactCsvStore());

        controller.AddContact();

        Assert.AreEqual(1, view.Rows.Count);
        Assert.AreEqual(1, view.Rows[0].ImportOrdinal);
        Assert.AreEqual("New person", view.Rows[0].Name);
        Assert.IsFalse(view.Rows[0].IsCheckedRecipient);
        Assert.AreEqual(1, view.FocusedOrdinal);
        Assert.IsTrue(view.DocumentState!.IsDirty);
        Assert.AreEqual("Unsaved contacts", view.DocumentState.DisplayName);
        Assert.IsTrue(view.InteractionState!.CanSaveCsv);
    }

    [TestMethod]
    public void AddCancelLeavesUntitledDocumentUnchanged()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs { ContactResult = null };
        var controller = CreateWithStore(view, dialogs, new FakeContactCsvStore());

        controller.AddContact();

        Assert.AreEqual(0, view.Rows.Count);
        Assert.IsFalse(view.DocumentState?.IsDirty ?? false);
    }

    [TestMethod]
    public async Task EditRequiresExactlyOneHighlightAndNameOnlyPreservesCheckAndResult()
    {
        var view = new FakeMainView { MessageText = "hello" };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1), ValidRow(2) }));
        var controller = CreateWithStore(
            view,
            dialogs,
            store,
            coordinator: new ImmediateProgressCoordinator());
        await controller.ImportContactsAsync();

        view.HighlightedContactOrdinals = new[] { 1, 2 };
        controller.EditContact();
        Assert.IsNull(dialogs.LastContactRequest);
        StringAssert.Contains(view.Error!, "exactly one");

        controller.SelectAllEligible();
        await controller.SendAsync(SendScope.Selected);
        await WaitUntilAsync(() =>
            view.Rows.Single(row => row.ImportOrdinal == 1).ProviderMessageId == "SM-1");
        view.HighlightedContactOrdinals = new[] { 1 };
        dialogs.ContactResult = new("Renamed", "+15550100101");
        controller.EditContact();

        var edited = view.Rows.Single(row => row.ImportOrdinal == 1);
        Assert.AreEqual("Renamed", edited.Name);
        Assert.IsTrue(edited.IsCheckedRecipient);
        Assert.AreEqual("SM-1", edited.ProviderMessageId);
        Assert.AreEqual(1, view.FocusedOrdinal);
    }

    [TestMethod]
    public async Task SendWithoutSynchronizationContextDrainsDelayedProgressBeforeSettlement()
    {
        Assert.IsNull(SynchronizationContext.Current);
        var view = new FakeMainView
        {
            MessageText = "hello",
            DelayProgressApplication = true
        };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) }));
        var controller = CreateWithStore(
            view,
            dialogs,
            store,
            coordinator: new BackgroundDelayedProgressCoordinator());
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();

        var send = controller.SendAsync(SendScope.Selected);
        await view.ProgressApplicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(send.IsCompleted);
        Assert.IsTrue(controller.IsBatchActive);
        Assert.IsNull(view.Summary);

        view.ReleaseProgressApplication();
        await send;

        Assert.IsFalse(controller.IsBatchActive);
        Assert.IsNotNull(view.Summary);
        Assert.AreEqual("SM-DELAYED", view.Rows.Single().ProviderMessageId);
        Assert.AreEqual(1, view.MaximumConcurrentProgress);
        CollectionAssert.AreEqual(
            new[]
            {
                RecipientSendState.Pending,
                RecipientSendState.Succeeded
            },
            view.Progress.Select(item => item.State).ToArray());
    }

    [TestMethod]
    public async Task NumberEditClearsOnlyEditedCheckAndResult()
    {
        var view = new FakeMainView { MessageText = "hello" };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1), ValidRow(2) }));
        var controller = CreateWithStore(
            view,
            dialogs,
            store,
            coordinator: new ImmediateProgressCoordinator());
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();
        await controller.SendAsync(SendScope.Selected);
        await WaitUntilAsync(() => view.Rows.All(row => row.ProviderMessageId is not null));

        view.HighlightedContactOrdinals = new[] { 1 };
        dialogs.ContactResult = new("Person 1", "+15550100999");
        controller.EditContact();

        var changed = view.Rows.Single(row => row.ImportOrdinal == 1);
        var unchanged = view.Rows.Single(row => row.ImportOrdinal == 2);
        Assert.IsFalse(changed.IsCheckedRecipient);
        Assert.IsNull(changed.SendState);
        Assert.IsTrue(unchanged.IsCheckedRecipient);
        Assert.AreEqual("SM-2", unchanged.ProviderMessageId);
    }

    [TestMethod]
    public async Task DeleteUsesHighlightsExactCountDefaultNoAndNearestSurvivor()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            DeleteConfirmationResult = false
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1), ValidRow(2), ValidRow(3) }));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();
        view.HighlightedContactOrdinals = new[] { 1, 2 };

        controller.DeleteSelectedContacts();
        Assert.AreEqual(3, view.Rows.Count);
        Assert.AreEqual(2, dialogs.LastDeleteCount);

        dialogs.DeleteConfirmationResult = true;
        controller.DeleteSelectedContacts();
        CollectionAssert.AreEqual(
            new[] { 3 },
            view.Rows.Select(row => row.ImportOrdinal).ToArray());
        Assert.AreEqual(3, view.FocusedOrdinal);
        CollectionAssert.AreEquivalent(new[] { 3 }, view.AppliedSelection.ToArray());
        Assert.IsTrue(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task DeleteAllLeavesValidDirtyHeaderOnlyDocument()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            DeleteConfirmationResult = true
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1), ValidRow(2) }));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1, 2 };

        controller.DeleteSelectedContacts();

        Assert.AreEqual(0, view.Rows.Count);
        Assert.IsTrue(view.DocumentState!.IsDirty);
        Assert.IsTrue(view.InteractionState!.CanSaveCsv);
    }

    [TestMethod]
    public async Task FirstSaveAsMarksCleanAndSettingsFailureWarnsWithoutRollback()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "saved.csv"
        };
        var store = new FakeContactCsvStore();
        var savedVersion = FakeContactCsvStore.Version(42, 'S');
        store.QueueSave(FakeContactCsvStore.SuccessfulSave("saved.csv", savedVersion));
        var settings = new ReadySettingsService
        {
            SaveCsvPathResult = new(
                SaveCsvPathStatus.StorageFailed,
                "settings unavailable")
        };
        var controller = CreateWithStore(view, dialogs, store, settings);
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(1, store.SaveRequests.Count);
        Assert.IsNull(store.SaveRequests[0].ExpectedVersion);
        Assert.IsFalse(view.DocumentState!.IsDirty);
        Assert.AreEqual("saved.csv", view.DocumentState.DisplayName);
        Assert.AreSame(savedVersion, view.DocumentState.Rows.Count >= 0
            ? savedVersion
            : null);
        StringAssert.Contains(view.Error!, "may not be remembered");
    }

    [TestMethod]
    public async Task SuccessfulSaveCleanupWarningIsVisibleAndDocumentRemainsClean()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "saved.csv"
        };
        var store = new FakeContactCsvStore();
        store.QueueSave(new ContactCsvSaveResult(
            ContactCsvSaveStatus.Saved,
            "saved.csv",
            FakeContactCsvStore.Version(42, 'S'),
            null,
            "Contacts were saved, but a temporary CSV file may remain."));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.IsFalse(view.DocumentState!.IsDirty);
        StringAssert.Contains(view.Error!, "temporary CSV file");
    }

    [TestMethod]
    public async Task SamePathSaveUsesLoadVersionAndPreservesSelectionAndResults()
    {
        var view = new FakeMainView { MessageText = "hello" };
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var store = new FakeContactCsvStore();
        var loadedVersion = FakeContactCsvStore.Version(10, 'L');
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) },
            loadedVersion));
        store.QueueSave(FakeContactCsvStore.SuccessfulSave(
            "contacts.csv",
            FakeContactCsvStore.Version(20, 'S')));
        var controller = CreateWithStore(
            view,
            dialogs,
            store,
            coordinator: new ImmediateProgressCoordinator());
        await controller.ImportContactsAsync();
        controller.SelectAllEligible();
        await controller.SendAsync(SendScope.Selected);
        await WaitUntilAsync(() => view.Rows[0].ProviderMessageId == "SM-1");
        view.HighlightedContactOrdinals = new[] { 1 };
        dialogs.ContactResult = new("Renamed", "+15550100101");
        controller.EditContact();

        await controller.SaveContactsAsync();

        Assert.IsTrue(loadedVersion == store.SaveRequests.Single().ExpectedVersion);
        Assert.IsFalse(view.DocumentState!.IsDirty);
        Assert.IsTrue(view.Rows[0].IsCheckedRecipient);
        Assert.AreEqual("SM-1", view.Rows[0].ProviderMessageId);
        CollectionAssert.AreEquivalent(new[] { 1 }, view.HighlightedContactOrdinals.ToArray());
    }

    [TestMethod]
    public async Task InvalidDirtyDocumentBlocksSaveAndFocusesFirstInvalid()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Valid", "+15550100101")
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[]
            {
                new ContactRow(
                    1,
                    string.Empty,
                    "+15550100100",
                    new[] { ContactErrorCode.NameRequired })
            }));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        controller.AddContact();

        Assert.IsFalse(view.InteractionState!.CanSaveCsv);
        await controller.SaveContactsAsync();
        Assert.AreEqual(0, store.SaveRequests.Count);
        Assert.AreEqual(1, view.FocusedOrdinal);
    }

    [TestMethod]
    public async Task FormulaPrefixImportedContactBlocksControllerSaveAndFocusesRow()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Valid", "+15550100101")
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[]
            {
                new ContactRow(
                    1,
                    "=HYPERLINK(\"https://example.invalid\")",
                    "+15550100100",
                    new[] { ContactErrorCode.FormulaPrefixNotAllowed })
            }));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.IsFalse(view.InteractionState!.CanSaveCsv);
        Assert.AreEqual(0, store.SaveRequests.Count);
        Assert.AreEqual(1, view.FocusedOrdinal);
        StringAssert.Contains(
            view.Rows.Single(row => row.ImportOrdinal == 1).ValidationText,
            "cannot begin");
    }

    [TestMethod]
    public async Task StoreLimitFailureUsesSafeDiagnosticWithoutMislabelingValidRows()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "target.csv"
        };
        var store = new FakeContactCsvStore();
        store.QueueSave(new ContactCsvSaveResult(
            ContactCsvSaveStatus.InvalidDocument,
            null,
            null,
            null,
            "CSV exceeds the 10,485,760 byte file size limit."));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();
        var focusedBeforeSave = view.FocusedOrdinal;

        await controller.SaveContactsAsync();

        Assert.IsTrue(view.DocumentState!.IsDirty);
        Assert.AreEqual(focusedBeforeSave, view.FocusedOrdinal);
        StringAssert.Contains(view.Error!, "byte file size limit");
    }

    [TestMethod]
    public async Task ModifiedConflictExplicitOverwriteRetriesAgainstObservedVersion()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Renamed", "+15550100101"),
            ConflictChoice = ExternalCsvConflictChoice.OverwriteThisVersion
        };
        var store = new FakeContactCsvStore();
        var loaded = FakeContactCsvStore.Version(10, 'A');
        var external = FakeContactCsvStore.Version(11, 'B');
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) },
            loaded));
        store.QueueSave(
            new(
                ContactCsvSaveStatus.ConflictModified,
                "contacts.csv",
                null,
                external,
                "changed"),
            FakeContactCsvStore.SuccessfulSave("contacts.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.IsTrue(loaded == store.SaveRequests[0].ExpectedVersion);
        Assert.IsTrue(external == store.SaveRequests[1].ExpectedVersion);
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task ReloadExternalAfterSaveAsChainUsesCanonicalConflictFullPath()
    {
        const string originalPath = "original.csv";
        const string selectedSaveAsPath = "relative-target.csv";
        const string canonicalTargetPath = @"C:\canonical\target.csv";
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = originalPath,
            ContactResult = new("Renamed", "+15550100101")
        };
        dialogs.ConflictChoices.Enqueue(ExternalCsvConflictChoice.SaveAs);
        dialogs.ConflictChoices.Enqueue(ExternalCsvConflictChoice.OverwriteThisVersion);
        dialogs.ConflictChoices.Enqueue(ExternalCsvConflictChoice.ReloadExternal);
        dialogs.SavePaths.Enqueue(selectedSaveAsPath);
        var store = new FakeContactCsvStore();
        var originalVersion = FakeContactCsvStore.Version(10, 'A');
        var targetVersionOne = FakeContactCsvStore.Version(11, 'B');
        var targetVersionTwo = FakeContactCsvStore.Version(12, 'C');
        store.QueueLoad(
            FakeContactCsvStore.SuccessfulLoad(
                originalPath,
                new[] { ValidRow(1) },
                originalVersion),
            FakeContactCsvStore.SuccessfulLoad(
                canonicalTargetPath,
                new[] { new ContactRow(
                    1,
                    "External",
                    "+15550100999",
                    Array.Empty<ContactErrorCode>()) },
                targetVersionTwo));
        store.QueueSave(
            new(
                ContactCsvSaveStatus.ConflictModified,
                Path.GetFullPath(originalPath),
                null,
                FakeContactCsvStore.Version(13, 'D'),
                "changed"),
            new(
                ContactCsvSaveStatus.TargetExists,
                canonicalTargetPath,
                null,
                targetVersionOne,
                "exists"),
            new(
                ContactCsvSaveStatus.ConflictModified,
                canonicalTargetPath,
                null,
                targetVersionTwo,
                "changed again"));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.SaveContactsAsync();

        CollectionAssert.AreEqual(
            new[] { originalPath, canonicalTargetPath },
            store.LoadPaths.ToArray());
        CollectionAssert.AreEqual(
            new[] { originalPath, selectedSaveAsPath, selectedSaveAsPath },
            store.SaveRequests.Select(request => request.Path).ToArray());
        Assert.AreEqual("External", view.Rows.Single().Name);
        Assert.AreEqual(Path.GetFileName(canonicalTargetPath), view.DocumentState!.DisplayName);
        Assert.IsFalse(view.DocumentState.IsDirty);
    }

    [TestMethod]
    public async Task DeletedConflictSaveAsRetriesChosenPathWithoutExpectedVersion()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Renamed", "+15550100101"),
            ConflictChoice = ExternalCsvConflictChoice.SaveAs,
            CsvSavePath = "replacement.csv"
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) }));
        store.QueueSave(
            new ContactCsvSaveResult(
                ContactCsvSaveStatus.ConflictDeleted,
                Path.GetFullPath("contacts.csv"),
                null,
                null,
                "deleted"),
            FakeContactCsvStore.SuccessfulSave("replacement.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.AreEqual("replacement.csv", store.SaveRequests[1].Path);
        Assert.IsNull(store.SaveRequests[1].ExpectedVersion);
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task TargetExistsOverwriteRetriesAgainstObservedVersion()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "target.csv",
            ConflictChoice = ExternalCsvConflictChoice.OverwriteThisVersion
        };
        var observed = FakeContactCsvStore.Version(20, 'E');
        var store = new FakeContactCsvStore();
        store.QueueSave(
            new ContactCsvSaveResult(
                ContactCsvSaveStatus.TargetExists,
                Path.GetFullPath("target.csv"),
                null,
                observed,
                "exists"),
            FakeContactCsvStore.SuccessfulSave("target.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.IsNull(store.SaveRequests[0].ExpectedVersion);
        Assert.IsTrue(ReferenceEquals(
            observed,
            store.SaveRequests[1].ExpectedVersion));
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    [DataRow(ContactCsvSaveStatus.ConflictModified)]
    [DataRow(ContactCsvSaveStatus.ConflictDeleted)]
    [DataRow(ContactCsvSaveStatus.TargetExists)]
    public async Task ConflictCancelPreservesDirtyDocumentWithoutRetry(
        ContactCsvSaveStatus status)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Renamed", "+15550100101"),
            ConflictChoice = ExternalCsvConflictChoice.Cancel
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) }));
        store.QueueSave(new ContactCsvSaveResult(
            status,
            Path.GetFullPath("contacts.csv"),
            null,
            status == ContactCsvSaveStatus.ConflictDeleted
                ? null
                : FakeContactCsvStore.Version(21, 'F'),
            "conflict"));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(1, store.SaveRequests.Count);
        Assert.IsTrue(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    [DataRow(ContactCsvSaveStatus.AccessDenied)]
    [DataRow(ContactCsvSaveStatus.IoFailure)]
    [DataRow(ContactCsvSaveStatus.AtomicReplaceUnavailable)]
    public async Task RecoverableSaveFailureCancelPreservesDirtyDocument(
        ContactCsvSaveStatus status)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "target.csv",
            FailureChoice = SaveFailureChoice.Cancel
        };
        var store = new FakeContactCsvStore();
        store.QueueSave(new ContactCsvSaveResult(
            status,
            Path.GetFullPath("target.csv"),
            null,
            null,
            "failed"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(1, store.SaveRequests.Count);
        Assert.IsTrue(view.DocumentState!.IsDirty);
        Assert.AreEqual("failed", view.Error);
    }

    [TestMethod]
    public async Task DeletedConflictExplicitRecreateRetriesWithoutExpectedVersion()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Renamed", "+15550100101"),
            ConflictChoice = ExternalCsvConflictChoice.Recreate
        };
        var store = new FakeContactCsvStore();
        var loaded = FakeContactCsvStore.Version(10, 'A');
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) },
            loaded));
        store.QueueSave(
            new(
                ContactCsvSaveStatus.ConflictDeleted,
                "contacts.csv",
                null,
                null,
                "deleted"),
            FakeContactCsvStore.SuccessfulSave("contacts.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.IsTrue(loaded == store.SaveRequests[0].ExpectedVersion);
        Assert.IsNull(store.SaveRequests[1].ExpectedVersion);
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task RepeatedConflictPromptsAgainAndCancelPreservesDirtyState()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Renamed", "+15550100101")
        };
        dialogs.ConflictChoices.Enqueue(
            ExternalCsvConflictChoice.OverwriteThisVersion);
        dialogs.ConflictChoices.Enqueue(ExternalCsvConflictChoice.Cancel);
        var store = new FakeContactCsvStore();
        var loaded = FakeContactCsvStore.Version(10, 'A');
        var externalOne = FakeContactCsvStore.Version(11, 'B');
        var externalTwo = FakeContactCsvStore.Version(12, 'C');
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "contacts.csv",
            new[] { ValidRow(1) },
            loaded));
        store.QueueSave(
            new(
                ContactCsvSaveStatus.ConflictModified,
                "contacts.csv",
                null,
                externalOne,
                "changed"),
            new(
                ContactCsvSaveStatus.ConflictModified,
                "contacts.csv",
                null,
                externalTwo,
                "changed again"));
        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(2, dialogs.ConflictPrompts.Count);
        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.IsTrue(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task DeletedConflictRecreateAndTargetExistsChooseAnotherAreExplicit()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "first.csv"
        };
        dialogs.ConflictChoices.Enqueue(ExternalCsvConflictChoice.ChooseAnother);
        dialogs.SavePaths.Enqueue("first.csv");
        dialogs.SavePaths.Enqueue("second.csv");
        var store = new FakeContactCsvStore();
        store.QueueSave(
            new(
                ContactCsvSaveStatus.TargetExists,
                "first.csv",
                null,
                FakeContactCsvStore.Version(1, 'E'),
                "exists"),
            FakeContactCsvStore.SuccessfulSave("second.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.SaveContactsAsync();

        CollectionAssert.AreEqual(
            new[] { "first.csv", "second.csv" },
            store.SaveRequests.Select(request => request.Path).ToArray());
        Assert.IsTrue(store.SaveRequests.All(request => request.ExpectedVersion is null));
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task SaveFailureCanRecoverThroughSaveAs()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "first.csv",
            FailureChoice = SaveFailureChoice.SaveAs
        };
        dialogs.SavePaths.Enqueue("first.csv");
        dialogs.SavePaths.Enqueue("second.csv");
        var store = new FakeContactCsvStore();
        store.QueueSave(
            new(
                ContactCsvSaveStatus.AtomicReplaceUnavailable,
                "first.csv",
                null,
                null,
                "unavailable"),
            FakeContactCsvStore.SuccessfulSave("second.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    [DataRow(ContactCsvSaveStatus.AccessDenied)]
    [DataRow(ContactCsvSaveStatus.IoFailure)]
    [DataRow(ContactCsvSaveStatus.AtomicReplaceUnavailable)]
    public async Task RecoverableSaveFailuresOfferSaveAs(
        ContactCsvSaveStatus failureStatus)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            FailureChoice = SaveFailureChoice.SaveAs
        };
        dialogs.SavePaths.Enqueue("first.csv");
        dialogs.SavePaths.Enqueue("second.csv");
        var store = new FakeContactCsvStore();
        store.QueueSave(
            new(failureStatus, "first.csv", null, null, "failed"),
            FakeContactCsvStore.SuccessfulSave("second.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.SaveContactsAsync();

        Assert.AreEqual(failureStatus, dialogs.FailurePrompts.Single().Status);
        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task CanceledFirstSaveAsPreservesDirtyDocumentAndState()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = null
        };
        var store = new FakeContactCsvStore();
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();
        var rows = view.Rows;

        await controller.SaveContactsAsync();

        Assert.AreEqual(0, store.SaveRequests.Count);
        Assert.AreSame(rows, view.Rows);
        Assert.IsTrue(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task DirtyImportCancelAndFailedDiscardPreserveCompleteDocument()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("Local", "+15550100100"),
            CsvPath = "external.csv"
        };
        var store = new FakeContactCsvStore();
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();
        var original = view.Rows;

        dialogs.UnsavedChoice = UnsavedChangesChoice.Cancel;
        await controller.ImportContactsAsync();
        Assert.AreEqual(0, store.LoadPaths.Count);
        Assert.AreSame(original, view.Rows);

        dialogs.UnsavedChoice = UnsavedChangesChoice.Discard;
        store.QueueLoad(new ContactCsvLoadResult(
            CsvImportStatus.MalformedCsv,
            null,
            Array.Empty<ContactRow>(),
            null,
            "Malformed."));
        await controller.ImportContactsAsync();
        Assert.AreSame(original, view.Rows);
        Assert.IsTrue(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task DirtyImportSaveMustSucceedBeforeReplacement()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("Local", "+15550100100"),
            CsvPath = "external.csv",
            CsvSavePath = "local.csv",
            UnsavedChoice = UnsavedChangesChoice.Save
        };
        var store = new FakeContactCsvStore();
        store.QueueSave(FakeContactCsvStore.SuccessfulSave("local.csv"));
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "external.csv",
            new[] { ValidRow(1) }));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.ImportContactsAsync();

        Assert.AreEqual(1, store.SaveRequests.Count);
        Assert.AreEqual(1, store.LoadPaths.Count);
        Assert.AreEqual("Person 1", view.Rows.Single().Name);
        Assert.IsFalse(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    [DataRow(UnsavedChangesChoice.Save, 1, 1, "Person 1", false)]
    [DataRow(UnsavedChangesChoice.Discard, 0, 1, "Person 1", false)]
    [DataRow(UnsavedChangesChoice.Cancel, 0, 0, "Local", true)]
    public async Task DirtyImportHonorsSaveDiscardAndCancel(
        UnsavedChangesChoice choice,
        int expectedSaveCount,
        int expectedLoadCount,
        string expectedName,
        bool expectedDirty)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("Local", "+15550100100"),
            CsvPath = "external.csv",
            CsvSavePath = "local.csv",
            UnsavedChoice = choice
        };
        var store = new FakeContactCsvStore();
        if (choice == UnsavedChangesChoice.Save)
        {
            store.QueueSave(FakeContactCsvStore.SuccessfulSave("local.csv"));
        }

        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad(
            "external.csv",
            new[] { ValidRow(1) }));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.ImportContactsAsync();

        Assert.AreEqual(PendingAction.Import, dialogs.LastUnsavedPrompt!.Value.Action);
        Assert.AreEqual(expectedSaveCount, store.SaveRequests.Count);
        Assert.AreEqual(expectedLoadCount, store.LoadPaths.Count);
        Assert.AreEqual(expectedName, view.Rows.Single().Name);
        Assert.AreEqual(expectedDirty, view.DocumentState!.IsDirty);
    }

    [TestMethod]
    [DataRow(UnsavedChangesChoice.Save, 1, 2, false)]
    [DataRow(UnsavedChangesChoice.Discard, 0, 2, false)]
    [DataRow(UnsavedChangesChoice.Cancel, 0, 1, true)]
    public async Task DirtyRefreshHonorsSaveDiscardAndCancel(
        UnsavedChangesChoice choice,
        int expectedSaveCount,
        int expectedLoadCount,
        bool expectedDirty)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            CsvPath = "contacts.csv",
            ContactResult = new("Local edit", "+15550100101"),
            UnsavedChoice = choice
        };
        var store = new FakeContactCsvStore();
        store.QueueLoad(
            FakeContactCsvStore.SuccessfulLoad(
                "contacts.csv",
                new[] { ValidRow(1) }),
            FakeContactCsvStore.SuccessfulLoad(
                "contacts.csv",
                new[]
                {
                    new ContactRow(
                        1,
                        "External refresh",
                        "+15550100999",
                        Array.Empty<ContactErrorCode>())
                }));
        if (choice == UnsavedChangesChoice.Save)
        {
            store.QueueSave(FakeContactCsvStore.SuccessfulSave("contacts.csv"));
        }

        var controller = CreateWithStore(view, dialogs, store);
        await controller.ImportContactsAsync();
        view.HighlightedContactOrdinals = new[] { 1 };
        controller.EditContact();

        await controller.RefreshContactsAsync();

        Assert.AreEqual(PendingAction.Refresh, dialogs.LastUnsavedPrompt!.Value.Action);
        Assert.AreEqual(expectedSaveCount, store.SaveRequests.Count);
        Assert.AreEqual(expectedLoadCount, store.LoadPaths.Count);
        Assert.AreEqual(expectedDirty, view.DocumentState!.IsDirty);
        Assert.AreEqual(
            choice == UnsavedChangesChoice.Cancel ? "Local edit" : "External refresh",
            view.Rows.Single().Name);
    }

    [TestMethod]
    [DataRow(UnsavedChangesChoice.Save, 1, 1)]
    [DataRow(UnsavedChangesChoice.Discard, 0, 1)]
    [DataRow(UnsavedChangesChoice.Cancel, 0, 0)]
    public async Task DirtyNonBatchExitHonorsSaveDiscardAndCancel(
        UnsavedChangesChoice choice,
        int expectedSaveCount,
        int expectedCloseCount)
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("Local", "+15550100100"),
            CsvSavePath = "saved.csv",
            UnsavedChoice = choice
        };
        var store = new FakeContactCsvStore();
        if (choice == UnsavedChangesChoice.Save)
        {
            store.QueueSave(FakeContactCsvStore.SuccessfulSave("saved.csv"));
        }

        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.RequestCloseAsync();

        Assert.AreEqual(PendingAction.Exit, dialogs.LastUnsavedPrompt!.Value.Action);
        Assert.AreEqual(expectedSaveCount, store.SaveRequests.Count);
        Assert.AreEqual(expectedCloseCount, view.CloseBypassCount);
        Assert.AreEqual(
            choice != UnsavedChangesChoice.Save,
            view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task DirtyRefreshAndExitAbortWhenRequestedSaveFails()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("Local", "+15550100100"),
            CsvSavePath = "saved.csv",
            UnsavedChoice = UnsavedChangesChoice.Save,
            FailureChoice = SaveFailureChoice.Cancel
        };
        var store = new FakeContactCsvStore();
        store.QueueSave(
            new ContactCsvSaveResult(
                ContactCsvSaveStatus.IoFailure,
                "saved.csv",
                null,
                null,
                "save failed"),
            new ContactCsvSaveResult(
                ContactCsvSaveStatus.IoFailure,
                "saved.csv",
                null,
                null,
                "save failed"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        await controller.RefreshContactsAsync();
        await controller.RequestCloseAsync();

        Assert.AreEqual(2, store.SaveRequests.Count);
        Assert.AreEqual(0, store.LoadPaths.Count);
        Assert.AreEqual(0, view.CloseBypassCount);
        Assert.IsTrue(view.DocumentState!.IsDirty);
    }

    [TestMethod]
    public async Task DelayedSaveRejectsDuplicateMutationsAndClose()
    {
        var view = new FakeMainView();
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CsvSavePath = "saved.csv"
        };
        var store = new FakeContactCsvStore { DelaySave = true };
        store.QueueSave(FakeContactCsvStore.SuccessfulSave("saved.csv"));
        var controller = CreateWithStore(view, dialogs, store);
        controller.AddContact();

        var save = controller.SaveContactsAsync();
        await store.SaveStarted.Task;
        Assert.IsFalse(view.InteractionState!.CanAddContact);
        Assert.IsFalse(view.InteractionState.CanSaveCsv);
        controller.AddContact();
        await controller.SaveContactsAsync();
        await controller.RequestCloseAsync();
        Assert.AreEqual(1, store.SaveRequests.Count);
        Assert.AreEqual(1, view.Rows.Count);
        Assert.AreEqual(0, view.CloseBypassCount);

        store.ReleaseSave();
        await save;
    }

    [TestMethod]
    public async Task ActiveBatchSettlesBeforeDirtyExitGuardAndClosesOnce()
    {
        var view = new FakeMainView { MessageText = "hello" };
        var dialogs = new FakeUserDialogs
        {
            ContactResult = new("One", "+15550100100"),
            CloseChoice = CloseDuringBatchChoice.CancelRemainingAndClose,
            UnsavedChoice = UnsavedChangesChoice.Discard
        };
        var coordinator = new RecordingCoordinator { Delay = true };
        var controller = CreateWithStore(
            view,
            dialogs,
            new FakeContactCsvStore(),
            coordinator: coordinator);
        controller.AddContact();
        controller.SelectAllEligible();
        var send = controller.SendAsync(SendScope.Selected);
        await coordinator.Started.Task;

        var close = controller.RequestCloseAsync();
        Assert.AreEqual(0, dialogs.UnsavedConfirmationCount);
        coordinator.Complete(canceled: true);
        await Task.WhenAll(send, close);

        Assert.AreEqual(1, dialogs.UnsavedConfirmationCount);
        Assert.AreEqual(PendingAction.Exit, dialogs.LastUnsavedPrompt!.Value.Action);
        Assert.AreEqual(1, view.CloseBypassCount);
        await controller.RequestCloseAsync();
        Assert.AreEqual(1, view.CloseBypassCount);
    }

    private static MainController Create(
        FakeMainView? view = null,
        FakeUserDialogs? dialogs = null,
        IContactCsvStore? importer = null,
        ISettingsService? settings = null,
        IBatchSendCoordinator? coordinator = null) =>
        new(view ?? new FakeMainView(),
            dialogs ?? new FakeUserDialogs(),
            importer ?? new StubContactCsvStore(new(CsvImportStatus.Success, Array.Empty<ContactRow>(), null)),
            settings ?? new ReadySettingsService(),
            new MessageValidator(),
            coordinator ?? new RecordingCoordinator(),
            new FakeTimeProvider(Now),
            isSafeDemo: true);

    private static MainController CreateWithStore(
        FakeMainView? view = null,
        FakeUserDialogs? dialogs = null,
        IContactCsvStore? store = null,
        ISettingsService? settings = null,
        IBatchSendCoordinator? coordinator = null)
    {
        var phone = new E164PhoneNumberValidator();
        var rowValidator = new ContactRowValidator(phone);
        return new(
            view ?? new FakeMainView(),
            dialogs ?? new FakeUserDialogs(),
            store ?? new FakeContactCsvStore(),
            settings ?? new ReadySettingsService(),
            new MessageValidator(),
            coordinator ?? new RecordingCoordinator(),
            new FakeTimeProvider(Now),
            isSafeDemo: true,
            new ContactDraftValidator(phone),
            rowValidator);
    }

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

    private sealed record TestLoadResult(
        CsvImportStatus Status,
        IReadOnlyList<ContactRow> Rows,
        string? SafeDiagnostic);

    private sealed class StubContactCsvStore(TestLoadResult result) : IContactCsvStore
    {
        public int CallCount { get; private set; }
        public Task<ContactCsvLoadResult> LoadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(ToLoadResult(path, result));
        }

        public Task<ContactCsvSaveResult> SaveAsync(
            ContactCsvSaveRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ContactCsvSaveResult(
                ContactCsvSaveStatus.IoFailure,
                null,
                null,
                null,
                "Save is not configured."));
    }

    private sealed class SequenceContactCsvStore(params TestLoadResult[] results) : IContactCsvStore
    {
        private readonly Queue<TestLoadResult> _results = new(results);
        public List<string> Paths { get; } = [];

        public Task<ContactCsvLoadResult> LoadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(ToLoadResult(path, _results.Dequeue()));
        }

        public Task<ContactCsvSaveResult> SaveAsync(
            ContactCsvSaveRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ContactCsvSaveResult(
                ContactCsvSaveStatus.IoFailure,
                null,
                null,
                null,
                "Save is not configured."));
    }

    private sealed class ControllableContactCsvStore(TestLoadResult result) : IContactCsvStore
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ContactCsvLoadResult> LoadAsync(
            string path,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await _release.Task;
            return ToLoadResult(path, result);
        }

        public void Release() => _release.TrySetResult();

        public Task<ContactCsvSaveResult> SaveAsync(
            ContactCsvSaveRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ContactCsvSaveResult(
                ContactCsvSaveStatus.IoFailure,
                null,
                null,
                null,
                "Save is not configured."));
    }

    private static ContactCsvLoadResult ToLoadResult(
        string path,
        TestLoadResult result) =>
        result.Status == CsvImportStatus.Success
            ? new(
                result.Status,
                path,
                result.Rows,
                FakeContactCsvStore.Version(result.Rows.Count, '0'),
                result.SafeDiagnostic)
            : new(
                result.Status,
                null,
                Array.Empty<ContactRow>(),
                null,
                result.SafeDiagnostic);

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

    private sealed class ImmediateProgressCoordinator : IBatchSendCoordinator
    {
        public async Task<BatchRunResult> TryRunAsync(
            SmsBatchRequest request,
            IProgress<RecipientProgress> progress,
            CancellationToken cancellationToken)
        {
            foreach (var recipient in request.Recipients)
            {
                progress.Report(new(
                    request.BatchId,
                    recipient.ImportOrdinal,
                    RecipientSendState.Succeeded,
                    $"SM-{recipient.ImportOrdinal}",
                    null,
                    null));
            }

            await Task.Delay(25, cancellationToken);
            return new BatchRunResult(
                BatchStartStatus.Completed,
                new(
                    request.BatchId,
                    request.Recipients.Count,
                    request.Recipients.Count,
                    0,
                    0));
        }
    }

    private sealed class BackgroundDelayedProgressCoordinator : IBatchSendCoordinator
    {
        public async Task<BatchRunResult> TryRunAsync(
            SmsBatchRequest request,
            IProgress<RecipientProgress> progress,
            CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                var recipient = request.Recipients.Single();
                progress.Report(new(
                    request.BatchId,
                    recipient.ImportOrdinal,
                    RecipientSendState.Pending,
                    null,
                    null,
                    null));
                progress.Report(new(
                    request.BatchId,
                    recipient.ImportOrdinal,
                    RecipientSendState.Succeeded,
                    "SM-DELAYED",
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
