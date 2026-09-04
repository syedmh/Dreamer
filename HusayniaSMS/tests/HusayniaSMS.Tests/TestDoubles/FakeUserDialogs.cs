using HusayniaSMS.Core.Batching;
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
}
