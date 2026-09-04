using HusayniaSMS.Core.Batching;
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
}
