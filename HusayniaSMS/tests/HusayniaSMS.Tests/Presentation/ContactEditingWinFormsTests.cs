using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.Forms;
using HusayniaSMS.WinForms.Presentation;
using HusayniaSMS.Tests.TestDoubles;

namespace HusayniaSMS.Tests.Presentation;

[TestClass]
public sealed class ContactEditingWinFormsTests
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int MouseKeyLeftButton = 0x0001;

    private static readonly string[] ButtonFields =
    [
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
    ];

    [TestMethod]
    [DataRow(0d)]
    [DataRow(12d)]
    [DataRow(16d)]
    public void AllTwelveButtonsUseSharedDpiSafeSizing(double fontSize) =>
        RunSta(form =>
        {
            if (fontSize > 0)
            {
                form.Font = new Font(form.Font.FontFamily, (float)fontSize);
            }

            form.PerformLayout();
            Application.DoEvents();
            Assert.AreEqual(12, ButtonFields.Length);
            var first = GetField<Button>(form, ButtonFields[0]);
            var commonMinimumHeight = first.MinimumSize.Height;
            var commonPadding = first.Padding;
            Assert.IsTrue(form.DeviceDpi > 0);
            foreach (var fieldName in ButtonFields)
            {
                var button = GetField<Button>(form, fieldName);
                var text = TextRenderer.MeasureText(
                    button.Text,
                    button.Font,
                    Size.Empty,
                    TextFormatFlags.SingleLine);
                var preferred = button.GetPreferredSize(Size.Empty);
                Assert.IsTrue(button.AutoSize, fieldName);
                Assert.AreEqual(AutoSizeMode.GrowAndShrink, button.AutoSizeMode, fieldName);
                Assert.AreEqual(commonMinimumHeight, button.MinimumSize.Height, fieldName);
                Assert.AreEqual(commonPadding, button.Padding, fieldName);
                Assert.IsTrue(preferred.Width >= text.Width + button.Padding.Horizontal, fieldName);
                Assert.IsTrue(preferred.Height >= text.Height + button.Padding.Vertical, fieldName);
                Assert.IsTrue(button.Width >= preferred.Width, fieldName);
                Assert.IsTrue(button.Height >= preferred.Height, fieldName);
                Assert.IsTrue(
                    button.ClientSize.Width - button.Padding.Horizontal >= text.Width,
                    fieldName);
                Assert.IsTrue(
                    button.ClientSize.Height - button.Padding.Vertical >= text.Height,
                    fieldName);
            }
        });

    [TestMethod]
    public void GridKeepsContactCellsReadOnlyAndSeparatesChecksFromHighlights() =>
        RunSta(form =>
        {
            var rows = new[]
            {
                Row(1, isChecked: true),
                Row(2, isChecked: false)
            };
            form.RenderDocumentState(new(
                "contacts.csv",
                false,
                rows,
                new HashSet<int> { 1 },
                new HashSet<int> { 2 }));

            var grid = GetField<DataGridView>(form, "contactsGrid");
            Assert.AreEqual(DataGridViewSelectionMode.FullRowSelect, grid.SelectionMode);
            Assert.IsTrue(grid.MultiSelect);
            Assert.IsTrue(grid.Columns["Name"]!.ReadOnly);
            Assert.IsTrue(grid.Columns["Number"]!.ReadOnly);
            Assert.IsTrue(Convert.ToBoolean(grid.Rows[0].Cells["Selected"].Value));
            Assert.IsFalse(Convert.ToBoolean(grid.Rows[1].Cells["Selected"].Value));
            CollectionAssert.AreEqual(new[] { 1 }, form.CheckedRecipientOrdinals.ToArray());
            CollectionAssert.AreEqual(new[] { 2 }, form.HighlightedContactOrdinals.ToArray());
        });

    [TestMethod]
    public void RealCheckboxClickWithNoHighlightPreservesNoHighlightAndContactActions() =>
        RunSta(form =>
        {
            var (_, view) = ImportContacts(form, Contact(1), Contact(2), Contact(3));
            var grid = GetField<DataGridView>(form, "contactsGrid");
            var edit = GetField<Button>(form, "editContactButton");
            var delete = GetField<Button>(form, "deleteSelectedButton");
            grid.ClearSelection();
            Application.DoEvents();
            var editEnabled = edit.Enabled;
            var deleteEnabled = delete.Enabled;

            ClickCheckbox(grid, rowIndex: 0);

            Assert.IsTrue(Convert.ToBoolean(grid.Rows[0].Cells["Selected"].Value));
            CollectionAssert.AreEqual(
                Array.Empty<int>(),
                form.HighlightedContactOrdinals.Order().ToArray());
            Assert.AreEqual(editEnabled, edit.Enabled);
            Assert.AreEqual(deleteEnabled, delete.Enabled);
            Assert.AreEqual(1, view.CheckedRecipientReadCount);
        });

    [TestMethod]
    public void RealCheckboxClickPreservesDifferentHighlightedRowAndContactActions() =>
        RunSta(form =>
        {
            var (_, view) = ImportContacts(form, Contact(1), Contact(2), Contact(3));
            var grid = GetField<DataGridView>(form, "contactsGrid");
            var edit = GetField<Button>(form, "editContactButton");
            var delete = GetField<Button>(form, "deleteSelectedButton");
            SetHighlightedRows(grid, 1);
            var editEnabled = edit.Enabled;
            var deleteEnabled = delete.Enabled;
            view.ResetCheckedRecipientReadCount();

            ClickCheckbox(grid, rowIndex: 0);

            Assert.IsTrue(Convert.ToBoolean(grid.Rows[0].Cells["Selected"].Value));
            CollectionAssert.AreEqual(
                new[] { 2 },
                form.HighlightedContactOrdinals.Order().ToArray());
            Assert.AreEqual(editEnabled, edit.Enabled);
            Assert.AreEqual(deleteEnabled, delete.Enabled);
            Assert.AreEqual(1, view.CheckedRecipientReadCount);
        });

    [TestMethod]
    public void RealCheckboxClickPreservesMultipleHighlightedRowsAndContactActions() =>
        RunSta(form =>
        {
            var (_, view) = ImportContacts(form, Contact(1), Contact(2), Contact(3));
            var grid = GetField<DataGridView>(form, "contactsGrid");
            var edit = GetField<Button>(form, "editContactButton");
            var delete = GetField<Button>(form, "deleteSelectedButton");
            SetHighlightedRows(grid, 1, 2);
            var editEnabled = edit.Enabled;
            var deleteEnabled = delete.Enabled;
            view.ResetCheckedRecipientReadCount();

            ClickCheckbox(grid, rowIndex: 0);

            Assert.IsTrue(Convert.ToBoolean(grid.Rows[0].Cells["Selected"].Value));
            CollectionAssert.AreEqual(
                new[] { 2, 3 },
                form.HighlightedContactOrdinals.Order().ToArray());
            Assert.AreEqual(editEnabled, edit.Enabled);
            Assert.AreEqual(deleteEnabled, delete.Enabled);
            Assert.AreEqual(1, view.CheckedRecipientReadCount);
        });

    [TestMethod]
    public void RealNonCheckboxClickContinuesToControlHighlightedRows() =>
        RunSta(form =>
        {
            ImportContacts(form, Contact(1), Contact(2), Contact(3));
            var grid = GetField<DataGridView>(form, "contactsGrid");
            var edit = GetField<Button>(form, "editContactButton");
            var delete = GetField<Button>(form, "deleteSelectedButton");
            SetHighlightedRows(grid, 1);

            ClickCell(grid, rowIndex: 0, columnName: "Name");

            CollectionAssert.AreEqual(
                new[] { 1 },
                form.HighlightedContactOrdinals.Order().ToArray());
            Assert.IsTrue(edit.Enabled);
            Assert.IsTrue(delete.Enabled);
        });

    [TestMethod]
    public void FiveThousandRowSelectAllAndClearUseOneCheckedRecipientSynchronizationEach() =>
        RunSta(form =>
        {
            var contacts = Enumerable.Range(1, 5_000)
                .Select(Contact)
                .ToArray();
            var (controller, view) = ImportContacts(form, contacts);

            view.ResetCheckedRecipientReadCount();
            var selectAllTimer = Stopwatch.StartNew();
            controller.SelectAllEligible();
            selectAllTimer.Stop();

            Assert.AreEqual(1, view.CheckedRecipientReadCount);
            Assert.AreEqual(5_000, form.CheckedRecipientOrdinals.Count);
            Assert.IsTrue(
                selectAllTimer.Elapsed < TimeSpan.FromSeconds(20),
                $"Select All Valid took {selectAllTimer.Elapsed}.");

            view.ResetCheckedRecipientReadCount();
            var clearTimer = Stopwatch.StartNew();
            controller.ClearSelection();
            clearTimer.Stop();

            Assert.AreEqual(1, view.CheckedRecipientReadCount);
            Assert.AreEqual(0, form.CheckedRecipientOrdinals.Count);
            Assert.IsTrue(
                clearTimer.Elapsed < TimeSpan.FromSeconds(20),
                $"Clear Selection took {clearTimer.Elapsed}.");
        });

    [TestMethod]
    public void DirtyDocumentRendersTitleStatusAndValidationWithoutContactValues() =>
        RunSta(form =>
        {
            const string privateNumber = "+15550100999";
            form.RenderDocumentState(new(
                "contacts.csv",
                true,
                new[]
                {
                    Row(1, isChecked: false),
                    new ContactGridRowViewModel(
                        2,
                        false,
                        false,
                        "Invalid",
                        privateNumber,
                        "Number must be in E.164 format.",
                        null,
                        null,
                        null,
                        null)
                },
                new HashSet<int>(),
                new HashSet<int>()));

            Assert.AreEqual("Husaynia SMS — contacts.csv*", form.Text);
            var status = GetField<Label>(form, "contactsStatusLabel").Text;
            StringAssert.Contains(status, "2 contact(s)");
            StringAssert.Contains(status, "1 valid");
            StringAssert.Contains(status, "1 invalid");
            StringAssert.Contains(status, "Unsaved changes.");
            Assert.IsFalse(status.Contains(privateNumber, StringComparison.Ordinal));
        });

    [TestMethod]
    public void InteractionStateControlsAllContactActionsIndependently() =>
        RunSta(form =>
        {
            form.SetInteractionState(new(
                IsBatchActive: false,
                CanEditSetup: true,
                CanSaveSetup: true,
                CanImport: true,
                CanRefresh: true,
                CanAddContact: true,
                CanEditContact: false,
                CanDeleteContacts: true,
                CanSaveCsv: false,
                CanChangeSelection: true,
                CanEditMessage: true,
                CanSendSelected: true,
                CanSendAllValid: true,
                CanCancel: false));

            Assert.IsTrue(GetField<Button>(form, "addContactButton").Enabled);
            Assert.IsFalse(GetField<Button>(form, "editContactButton").Enabled);
            Assert.IsTrue(GetField<Button>(form, "deleteSelectedButton").Enabled);
            Assert.IsFalse(GetField<Button>(form, "saveCsvButton").Enabled);
        });

    [TestMethod]
    public void FocusContactSelectsOnlyRequestedRow() =>
        RunSta(form =>
        {
            form.RenderDocumentState(new(
                "contacts.csv",
                false,
                new[] { Row(1, false), Row(2, false), Row(3, false) },
                new HashSet<int>(),
                new HashSet<int>()));

            form.FocusContact(2);

            CollectionAssert.AreEqual(new[] { 2 }, form.HighlightedContactOrdinals.ToArray());
        });

    [TestMethod]
    public void ContactActionButtonsHaveAccessibleNames() =>
        RunSta(form =>
        {
            foreach (var fieldName in new[]
            {
                "addContactButton",
                "editContactButton",
                "deleteSelectedButton",
                "saveCsvButton"
            })
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(
                    GetField<Button>(form, fieldName).AccessibleName));
            }

            var addContact = GetField<Button>(form, "addContactButton");
            Assert.AreEqual("addContactButton", addContact.Name);
            Assert.AreEqual("Add Contact", addContact.AccessibleName);
            Assert.AreEqual("Add Contact", addContact.AccessibilityObject.Name);
            Assert.AreEqual(2, addContact.TabIndex);
            Assert.IsTrue(addContact.Enabled);
        });

    private static ContactGridRowViewModel Row(int ordinal, bool isChecked) =>
        new(
            ordinal,
            isChecked,
            true,
            $"Person {ordinal}",
            $"+1555010010{ordinal}",
            "Valid",
            ordinal == 1 ? RecipientSendState.Succeeded : null,
            ordinal == 1 ? "SM-1" : null,
            null,
            null);

    private static ContactRow Contact(int ordinal) =>
        new(
            ordinal,
            $"Person {ordinal}",
            $"+1555{ordinal:D7}",
            Array.Empty<ContactErrorCode>());

    private static (MainController Controller, CountingMainView View) ImportContacts(
        MainForm form,
        params ContactRow[] rows)
    {
        var view = new CountingMainView(form);
        var dialogs = new FakeUserDialogs { CsvPath = "contacts.csv" };
        var store = new FakeContactCsvStore();
        store.QueueLoad(FakeContactCsvStore.SuccessfulLoad("contacts.csv", rows));
        var controller = new MainController(
            view,
            dialogs,
            store,
            new ReadySettingsService(),
            new MessageValidator(),
            new NoOpBatchCoordinator(),
            TimeProvider.System,
            isSafeDemo: true);
        form.AttachController(controller);
        controller.ImportContactsAsync().GetAwaiter().GetResult();
        view.ResetCheckedRecipientReadCount();
        return (controller, view);
    }

    private static void SetHighlightedRows(
        DataGridView grid,
        params int[] rowIndexes)
    {
        grid.ClearSelection();
        foreach (var rowIndex in rowIndexes)
        {
            grid.Rows[rowIndex].Selected = true;
        }

        Application.DoEvents();
    }

    private static void ClickCheckbox(DataGridView grid, int rowIndex)
    {
        ClickCell(grid, rowIndex, "Selected");
    }

    private static void ClickCell(
        DataGridView grid,
        int rowIndex,
        string columnName)
    {
        grid.Focus();
        var cell = grid.Rows[rowIndex].Cells[columnName];
        var rectangle = grid.GetCellDisplayRectangle(
            cell.ColumnIndex,
            rowIndex,
            cutOverflow: true);
        var content = grid.Rows[rowIndex].Cells["Selected"].ContentBounds;
        var x = columnName == "Selected"
            ? rectangle.Left + content.Left + (content.Width / 2)
            : rectangle.Left + (rectangle.Width / 2);
        var y = columnName == "Selected"
            ? rectangle.Top + content.Top + (content.Height / 2)
            : rectangle.Top + (rectangle.Height / 2);
        var hit = grid.HitTest(x, y);
        Assert.AreEqual(rowIndex, hit.RowIndex);
        Assert.AreEqual(cell.ColumnIndex, hit.ColumnIndex);
        var screenPoint = grid.PointToScreen(new Point(x, y));
        Assert.IsTrue(SetCursorPosition(screenPoint.X, screenPoint.Y));
        var coordinates = (nint)((y << 16) | (x & 0xFFFF));
        SendMessage(grid.Handle, WmLeftButtonDown, (nint)MouseKeyLeftButton, coordinates);
        SendMessage(grid.Handle, WmLeftButtonUp, nint.Zero, coordinates);
        Application.DoEvents();
    }

    private static T GetField<T>(MainForm form, string name) where T : class
    {
        var field = typeof(MainForm).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(form)!;
    }

    private static void RunSta(Action<MainForm> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new MainForm(isSafeDemo: true);
                form.Show();
                Application.DoEvents();
                action(form);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter);

    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPosition(int x, int y);

    private sealed class CountingMainView(MainForm form) : IMainView
    {
        public int CheckedRecipientReadCount { get; private set; }
        public string MessageText => form.MessageText;
        public IReadOnlyList<int> CheckedRecipientOrdinals
        {
            get
            {
                CheckedRecipientReadCount++;
                return form.CheckedRecipientOrdinals;
            }
        }

        public IReadOnlyList<int> HighlightedContactOrdinals =>
            form.HighlightedContactOrdinals;

        public void ResetCheckedRecipientReadCount() =>
            CheckedRecipientReadCount = 0;
        public SetupInput ReadSetupInput() => form.ReadSetupInput();
        public void RenderSettings(
            SettingsDescriptor descriptor,
            string savedTokenPlaceholder) =>
            form.RenderSettings(descriptor, savedTokenPlaceholder);
        public void RenderDocumentState(ContactDocumentViewState state) =>
            form.RenderDocumentState(state);
        public void ApplyCheckedRecipients(IReadOnlySet<int> checkedOrdinals) =>
            form.ApplyCheckedRecipients(checkedOrdinals);
        public void FocusContact(int ordinal) => form.FocusContact(ordinal);
        public void RenderMessageValidation(MessageValidationResult result) =>
            form.RenderMessageValidation(result);
        public void SetInteractionState(MainInteractionState state) =>
            form.SetInteractionState(state);
        public void BeginBatch(
            Guid batchId,
            SendScope scope,
            int confirmedCount,
            DateTimeOffset startedAt) =>
            form.BeginBatch(batchId, scope, confirmedCount, startedAt);
        public void ApplyRecipientProgress(RecipientProgress progress) =>
            form.ApplyRecipientProgress(progress);
        public void EndBatch(BatchSummary summary) => form.EndBatch(summary);
        public void ShowSafeStatus(string message) => form.ShowSafeStatus(message);
        public void ShowSafeError(string message) => form.ShowSafeError(message);
        public void CloseAfterControllerApproval() => form.CloseAfterControllerApproval();
    }

    private sealed class ReadySettingsService : ISettingsService
    {
        public Task<SettingsDescriptor> LoadDescriptorAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsDescriptor(
                "",
                TwilioSenderMode.FromPhoneNumber,
                "",
                TokenState.Missing));

        public Task<SaveSettingsResult> SaveAsync(
            SetupInput input,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SaveSettingsResult(
                SaveSettingsStatus.Saved,
                Array.Empty<FieldError>(),
                "saved"));

        public Task<SaveCsvPathResult> SaveLastCsvPathAsync(
            string path,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SaveCsvPathResult(
                SaveCsvPathStatus.Saved,
                "saved"));

        public Task<CredentialLoadResult> LoadCredentialsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialLoadResult(
                CredentialLoadStatus.Incomplete,
                null,
                Array.Empty<FieldError>(),
                "Setup incomplete."));
    }

    private sealed class NoOpBatchCoordinator : IBatchSendCoordinator
    {
        public Task<BatchRunResult> TryRunAsync(
            SmsBatchRequest request,
            IProgress<RecipientProgress> progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BatchRunResult(
                BatchStartStatus.Completed,
                new BatchSummary(request.BatchId, 0, 0, 0, 0)));
    }
}
