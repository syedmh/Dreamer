using HusayniaSMS.Core.Batching;
using HusayniaSMS.Core.Contacts;
using HusayniaSMS.WinForms.Presentation;

namespace HusayniaSMS.WinForms.Forms;

public sealed class WinFormsUserDialogs(IWin32Window owner) : IUserDialogs
{
    public string? SelectCsvPath()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import contacts CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public bool ConfirmSend(SendScope scope, int recipientCount, bool isSafeDemo)
    {
        var scopeText = scope == SendScope.Selected ? "Selected" : "All valid";
        var action = isSafeDemo ? "run the safe no-send demo" : "send live SMS messages";
        return MessageBox.Show(owner,
            $"{scopeText} scope: {recipientCount} recipient(s).\n\nThis will {action}. Continue?",
            isSafeDemo ? "Confirm safe demo" : "Confirm live SMS send",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    public CloseDuringBatchChoice ConfirmCloseDuringBatch()
    {
        var result = MessageBox.Show(owner,
            "A send batch is active and one request may be in flight.\n\nYes: cancel remaining work and close after settlement.\nNo: stay open.",
            "Send batch active", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes
            ? CloseDuringBatchChoice.CancelRemainingAndClose
            : CloseDuringBatchChoice.Stay;
    }

    public ContactDialogResult? ShowContactDialog(
        ContactDialogRequest request,
        IContactDraftValidator validator)
    {
        using var dialog = new ContactDialog(request, validator);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.ContactResult
            : null;
    }

    public bool ConfirmDeleteContacts(int count) =>
        MessageBox.Show(
            owner,
            $"Delete exactly {count} highlighted contact(s)?",
            "Confirm contact deletion",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public UnsavedChangesChoice ConfirmUnsavedChanges(
        PendingAction action,
        bool canSave)
    {
        var actionText = action switch
        {
            PendingAction.Import => "importing another CSV",
            PendingAction.Refresh => "refreshing contacts",
            PendingAction.Exit => "exiting",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.")
        };
        var prompt = canSave
            ? $"Save contact changes before {actionText}?\n\nYes: Save\nNo: Discard\nCancel: Keep editing"
            : $"Contact changes cannot be saved until invalid or duplicate rows are corrected.\n\nDiscard changes before {actionText}?";
        var result = MessageBox.Show(
            owner,
            prompt,
            "Unsaved contact changes",
            canSave ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            canSave
                ? MessageBoxDefaultButton.Button3
                : MessageBoxDefaultButton.Button2);
        if (!canSave)
        {
            return result == DialogResult.OK
                ? UnsavedChangesChoice.Discard
                : UnsavedChangesChoice.Cancel;
        }

        return result switch
        {
            DialogResult.Yes => UnsavedChangesChoice.Save,
            DialogResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel
        };
    }

    public string? SelectCsvSavePath(string? suggestedPath)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save contacts CSV",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = "csv",
            OverwritePrompt = false,
            FileName = string.IsNullOrWhiteSpace(suggestedPath)
                ? "contacts.csv"
                : Path.GetFileName(suggestedPath),
            InitialDirectory = string.IsNullOrWhiteSpace(suggestedPath)
                ? string.Empty
                : Path.GetDirectoryName(suggestedPath)
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public ExternalCsvConflictChoice ResolveExternalCsvConflict(
        ExternalCsvConflictKind kind,
        string fileName)
    {
        var choices = kind switch
        {
            ExternalCsvConflictKind.Modified => new[]
            {
                ("Reload External", ExternalCsvConflictChoice.ReloadExternal),
                ("Overwrite This Version", ExternalCsvConflictChoice.OverwriteThisVersion),
                ("Save As", ExternalCsvConflictChoice.SaveAs),
                ("Cancel", ExternalCsvConflictChoice.Cancel)
            },
            ExternalCsvConflictKind.Deleted => new[]
            {
                ("Recreate", ExternalCsvConflictChoice.Recreate),
                ("Save As", ExternalCsvConflictChoice.SaveAs),
                ("Cancel", ExternalCsvConflictChoice.Cancel)
            },
            ExternalCsvConflictKind.TargetExists => new[]
            {
                ("Overwrite This Version", ExternalCsvConflictChoice.OverwriteThisVersion),
                ("Choose Another", ExternalCsvConflictChoice.ChooseAnother),
                ("Cancel", ExternalCsvConflictChoice.Cancel)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown conflict.")
        };
        return ShowChoice(
            "CSV file conflict",
            $"The CSV file '{fileName}' changed outside Husaynia SMS. Choose how to continue.",
            choices,
            ExternalCsvConflictChoice.Cancel);
    }

    public SaveFailureChoice ResolveSaveFailure(
        ContactCsvSaveStatus status,
        string fileName)
    {
        var result = MessageBox.Show(
            owner,
            $"The CSV file '{fileName}' could not be saved safely ({status}).\n\nChoose Yes to Save As, or No to cancel.",
            "CSV save failed",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Error,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes
            ? SaveFailureChoice.SaveAs
            : SaveFailureChoice.Cancel;
    }

    private T ShowChoice<T>(
        string title,
        string prompt,
        IReadOnlyList<(string Text, T Value)> choices,
        T cancelValue)
        where T : struct
    {
        using var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Font,
            ClientSize = new Size(620, 150),
            AccessibleName = title
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = prompt,
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            AccessibleName = prompt
        });
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        var selected = cancelValue;
        foreach (var choice in choices.Reverse())
        {
            var button = new Button
            {
                Text = choice.Text,
                AccessibleName = choice.Text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(0, 36),
                Padding = new Padding(12, 6, 12, 6)
            };
            button.Click += (_, _) =>
            {
                selected = choice.Value;
                dialog.DialogResult = DialogResult.OK;
            };
            actions.Controls.Add(button);
            if (EqualityComparer<T>.Default.Equals(choice.Value, cancelValue))
            {
                dialog.CancelButton = button;
            }
        }

        layout.Controls.Add(actions, 0, 1);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(owner);
        return selected;
    }
}
