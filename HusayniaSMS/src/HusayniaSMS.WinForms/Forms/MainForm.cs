using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Messaging;
using HusayniaSMS.Core.Settings;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.WinForms.Forms;

public partial class MainForm : Form, IMainView
{
    private const string FromPhoneNumberDisplayName = "From phone number";
    private const string MessagingServiceSidDisplayName = "Messaging Service SID";
    private const string FromPhoneNumberLabel = "From phone number (E.164)";
    private const string MessagingServiceSidLabel = "Messaging Service SID (MG...)";

    private MainController? _controller;
    private string _savedTokenPlaceholder = string.Empty;
    private bool _savedTokenAvailable;
    private bool _closeBypass;

    public MainForm(bool isSafeDemo)
    {
        InitializeComponent();
        safeDemoBanner.Visible = isSafeDemo;
    }

    public string MessageText => messageTextBox.Text;

    public IReadOnlyList<int> SelectedOrdinals =>
        contactsGrid.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Tag is ContactGridRowViewModel &&
                Convert.ToBoolean(row.Cells[selectedColumn.Index].Value))
            .Select(row => ((ContactGridRowViewModel)row.Tag!).ImportOrdinal)
            .ToArray();

    public void AttachController(MainController controller) => _controller = controller;

    public SetupInput ReadSetupInput()
    {
        var preserve = _savedTokenAvailable &&
            string.Equals(authTokenTextBox.Text, _savedTokenPlaceholder, StringComparison.Ordinal);
        return new(
            accountSidTextBox.Text,
            SelectedSenderMode,
            senderValueTextBox.Text,
            preserve ? null : authTokenTextBox.Text, preserve);
    }

    public void RenderSettings(SettingsDescriptor descriptor, string savedTokenPlaceholder)
    {
        _savedTokenPlaceholder = savedTokenPlaceholder;
        _savedTokenAvailable = descriptor.TokenState == TokenState.Available;
        accountSidTextBox.Text = descriptor.AccountSid;
        senderModeComboBox.SelectedIndex = descriptor.SenderMode switch
        {
            TwilioSenderMode.FromPhoneNumber => 0,
            TwilioSenderMode.MessagingServiceSid => 1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                descriptor.SenderMode,
                "The saved sender mode is not supported.")
        };
        senderValueTextBox.Text = descriptor.SenderValue;
        authTokenTextBox.Text = _savedTokenAvailable ? savedTokenPlaceholder : string.Empty;
        setupStatusLabel.Text = descriptor.TokenState switch
        {
            TokenState.Available =>
                $"Setup saved for {SenderModeDisplayName(descriptor.SenderMode)}; auth token is protected and available.",
            TokenState.Unavailable => "Saved token unavailable; re-enter it.",
            _ => "Setup incomplete."
        };
    }

    public void ReplaceContacts(IReadOnlyList<ContactGridRowViewModel> rows)
    {
        contactsGrid.Rows.Clear();
        foreach (var item in rows)
        {
            var index = contactsGrid.Rows.Add(item.IsSelected, item.Name, item.Number,
                item.ValidationText, item.SafeMessage ?? string.Empty, FormatResult(item));
            var row = contactsGrid.Rows[index];
            row.Tag = item;
            row.Cells[selectedColumn.Index].ReadOnly = !item.CanSelect;
            if (!item.CanSelect)
            {
                row.DefaultCellStyle.BackColor = Color.MistyRose;
            }
        }

        contactsStatusLabel.Text =
            $"{rows.Count} contact(s), {rows.Count(row => row.CanSelect)} valid.";
    }

    public void ApplySelection(IReadOnlySet<int> selectedOrdinals)
    {
        foreach (DataGridViewRow row in contactsGrid.Rows)
        {
            if (row.Tag is ContactGridRowViewModel item)
            {
                row.Cells[selectedColumn.Index].Value =
                    item.CanSelect && selectedOrdinals.Contains(item.ImportOrdinal);
            }
        }
    }

    public void RenderMessageValidation(MessageValidationResult result)
    {
        messageCountLabel.Text = $"{result.CharacterCount:N0} / 1,600 characters";
        messageErrorLabel.Text = result.ErrorMessage ?? string.Empty;
        messageErrorLabel.ForeColor = result.IsValid ? Color.DarkGreen : Color.DarkRed;
    }

    public void SetInteractionState(MainInteractionState state)
    {
        accountSidTextBox.Enabled = state.CanEditSetup;
        senderModeComboBox.Enabled = state.CanEditSetup;
        senderValueTextBox.Enabled = state.CanEditSetup;
        authTokenTextBox.Enabled = state.CanEditSetup;
        saveSetupButton.Enabled = state.CanSaveSetup;
        importButton.Enabled = state.CanImport;
        refreshButton.Enabled = state.CanRefresh;
        selectAllButton.Enabled = state.CanChangeSelection;
        clearSelectionButton.Enabled = state.CanChangeSelection;
        selectedColumn.ReadOnly = !state.CanChangeSelection;
        sendSelectedButton.Enabled = state.CanSendSelected;
        sendAllButton.Enabled = state.CanSendAllValid;
        cancelButton.Enabled = state.CanCancel;
        messageTextBox.Enabled = state.CanEditMessage;
    }

    public void BeginBatch(Guid batchId, SendScope scope, int confirmedCount, DateTimeOffset startedAt)
    {
        batchStatusLabel.Text =
            $"Batch {batchId:N} — {scope} — {confirmedCount} recipient(s) — started {startedAt.LocalDateTime:g}";
    }

    public void ApplyRecipientProgress(RecipientProgress progress)
    {
        foreach (DataGridViewRow row in contactsGrid.Rows)
        {
            if (row.Tag is not ContactGridRowViewModel item ||
                item.ImportOrdinal != progress.ImportOrdinal)
            {
                continue;
            }

            var updated = item with
            {
                SendState = progress.State,
                ProviderMessageId = progress.ProviderMessageId,
                SafeCode = progress.SafeCode,
                SafeMessage = progress.SafeMessage
            };
            row.Tag = updated;
            row.Cells[resultColumn.Index].Value = FormatResult(updated);
            break;
        }
    }

    public void EndBatch(BatchSummary summary)
    {
        batchStatusLabel.Text =
            $"Batch {summary.BatchId:N} complete — Confirmed {summary.Confirmed}; Succeeded {summary.Succeeded}; Failed {summary.Failed}; Canceled/not sent {summary.CanceledOrNotStarted}.";
    }

    public void ShowSafeStatus(string message)
    {
        applicationStatusLabel.ForeColor = Color.DarkGreen;
        applicationStatusLabel.Text = message;
    }

    public void ShowSafeError(string message)
    {
        applicationStatusLabel.ForeColor = Color.DarkRed;
        applicationStatusLabel.Text = message;
    }

    public void CloseWithBypass()
    {
        _closeBypass = true;
        Close();
    }

    private static string FormatResult(ContactGridRowViewModel item)
    {
        if (item.SendState is null)
        {
            return string.Empty;
        }

        var detail = item.SendState == RecipientSendState.Succeeded
            ? item.ProviderMessageId
            : item.SafeMessage is null
                ? item.SafeCode
                : item.SafeCode is null
                    ? item.SafeMessage
                    : $"{item.SafeMessage} ({item.SafeCode})";
        return detail is null ? item.SendState.Value.ToString() : $"{item.SendState}: {detail}";
    }

    private TwilioSenderMode SelectedSenderMode => senderModeComboBox.SelectedIndex switch
    {
        0 => TwilioSenderMode.FromPhoneNumber,
        1 => TwilioSenderMode.MessagingServiceSid,
        _ => throw new InvalidOperationException("Select a supported sender mode.")
    };

    private static string SenderModeDisplayName(TwilioSenderMode mode) => mode switch
    {
        TwilioSenderMode.FromPhoneNumber => FromPhoneNumberDisplayName,
        TwilioSenderMode.MessagingServiceSid => MessagingServiceSidDisplayName,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported sender mode.")
    };

    private void SenderModeComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        senderValueLabel.Text = SelectedSenderMode switch
        {
            TwilioSenderMode.FromPhoneNumber => FromPhoneNumberLabel,
            TwilioSenderMode.MessagingServiceSid => MessagingServiceSidLabel,
            _ => throw new InvalidOperationException("Select a supported sender mode.")
        };
    }

    private async void MainForm_Shown(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.InitializeAsync();
        }
    }

    private async void SaveSetupButton_Click(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.SaveSetupAsync();
        }
    }

    private async void ImportButton_Click(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.ImportContactsAsync();
        }
    }

    private async void RefreshButton_Click(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.RefreshContactsAsync();
        }
    }

    private void SelectAllButton_Click(object? sender, EventArgs e) => _controller?.SelectAllEligible();
    private void ClearSelectionButton_Click(object? sender, EventArgs e) => _controller?.ClearSelection();
    private void MessageTextBox_TextChanged(object? sender, EventArgs e) => _controller?.MessageChanged();

    private async void SendSelectedButton_Click(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.SendAsync(SendScope.Selected);
        }
    }

    private async void SendAllButton_Click(object? sender, EventArgs e)
    {
        if (_controller is not null)
        {
            await _controller.SendAsync(SendScope.AllValid);
        }
    }

    private void CancelButton_Click(object? sender, EventArgs e) => _controller?.CancelActiveBatch();

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closeBypass || _controller is null || !_controller.IsBatchActive)
        {
            return;
        }

        e.Cancel = true;
        await _controller.HandleActiveCloseRequestAsync();
    }
}
