using TCFPreview.Core;
using TCFPreview.WinForms;
using System.Reflection;
using System.Windows.Forms.Automation;

namespace TCFPreview.Tests;

[TestClass]
public sealed class MainFormPresentationTests
{
    [TestMethod]
    public void SourceSelection_LoadsFirstOrderedPhotoAndReportsPosition()
    {
        using TempDirectory directory = new();
        directory.CreateFile("zeta.jpg");
        directory.CreateFile("Alpha.png");
        directory.CreateFile("notes.txt");

        PhotoNavigator navigator = new(PhotoDiscovery.Discover(directory.Path));
        MainFormViewState state = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: false,
            copyInProgress: false,
            MainFormPresentation.SourceLoadedStatus(navigator.Count));

        Assert.AreEqual("Alpha.png", state.FileName);
        Assert.AreEqual("1 of 2", state.Position);
        Assert.AreEqual("Loaded 2 photos.", state.Status);
        Assert.IsFalse(state.CanMovePrevious);
        Assert.IsTrue(state.CanMoveNext);
        Assert.IsFalse(state.CanCopy);
    }

    [TestMethod]
    public void CreateViewState_ReportsFilenamePositionAndBoundaryButtonStates()
    {
        PhotoNavigator navigator = new(
            [@"C:\photos\one.jpg", @"C:\photos\two.png", @"C:\photos\three.tif"]);

        MainFormViewState first = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: true,
            copyInProgress: false,
            MainFormPresentation.PreviewReadyStatus);

        Assert.AreEqual("one.jpg", first.FileName);
        Assert.AreEqual("1 of 3", first.Position);
        Assert.IsFalse(first.CanMovePrevious);
        Assert.IsTrue(first.CanMoveNext);
        Assert.IsTrue(first.CanCopy);

        Assert.IsTrue(navigator.TryMoveNext(out _));
        MainFormViewState middle = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: true,
            copyInProgress: false,
            MainFormPresentation.PreviewReadyStatus);

        Assert.AreEqual("two.png", middle.FileName);
        Assert.AreEqual("2 of 3", middle.Position);
        Assert.IsTrue(middle.CanMovePrevious);
        Assert.IsTrue(middle.CanMoveNext);

        Assert.IsTrue(navigator.TryMoveNext(out _));
        MainFormViewState last = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: true,
            copyInProgress: false,
            MainFormPresentation.PreviewReadyStatus);

        Assert.AreEqual("three.tif", last.FileName);
        Assert.AreEqual("3 of 3", last.Position);
        Assert.IsTrue(last.CanMovePrevious);
        Assert.IsFalse(last.CanMoveNext);
    }

    [TestMethod]
    public void CreateViewState_DisablesCopyWithoutCurrentPhotoOrValidDestination()
    {
        PhotoNavigator emptyNavigator = new([]);
        MainFormViewState empty = MainFormPresentation.CreateViewState(
            emptyNavigator,
            destinationIsValid: true,
            copyInProgress: false,
            MainFormPresentation.SourceLoadedStatus(0));

        Assert.AreEqual("No photo selected", empty.FileName);
        Assert.AreEqual("0 of 0", empty.Position);
        Assert.IsFalse(empty.CanCopy);

        PhotoNavigator navigator = new(["photo.jpg"]);
        MainFormViewState missingDestination = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: false,
            copyInProgress: false,
            MainFormPresentation.PreviewReadyStatus);
        MainFormViewState copying = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: true,
            copyInProgress: true,
            MainFormPresentation.CopyingStatus);

        Assert.IsFalse(missingDestination.CanCopy);
        Assert.IsFalse(copying.CanCopy);
        Assert.IsFalse(copying.CanMovePrevious);
        Assert.IsFalse(copying.CanMoveNext);
        Assert.IsFalse(copying.CanBrowse);
    }

    [TestMethod]
    public void LoadPreview_ReturnsExplicitStatusForCorruptAndDeletedImages()
    {
        using TempDirectory directory = new();
        string corruptPath = directory.CreateFile("corrupt.jpg", [1, 2, 3, 4]);
        string deletedPath = directory.CreateFile("deleted.jpg", [5, 6, 7, 8]);
        File.Delete(deletedPath);

        PreviewPresentationResult corrupt = MainFormPresentation.LoadPreview(corruptPath);
        PreviewPresentationResult deleted = MainFormPresentation.LoadPreview(deletedPath);

        Assert.AreEqual(
            "No supported photos were found in this folder.",
            MainFormPresentation.SourceLoadedStatus(0));
        Assert.IsFalse(corrupt.IsAvailable);
        Assert.IsNull(corrupt.Image);
        StringAssert.StartsWith(corrupt.Status, "Preview unavailable:");
        Assert.IsFalse(deleted.IsAvailable);
        Assert.IsNull(deleted.Image);
        StringAssert.StartsWith(deleted.Status, "Preview unavailable:");
    }

    [TestMethod]
    public void UnavailablePreview_RetainsNavigationContextAndDisablesOnlyBoundaryActions()
    {
        using TempDirectory directory = new();
        string corruptPath = directory.CreateFile("corrupt.jpg", [1, 2, 3, 4]);
        string deletedPath = directory.CreateFile("deleted.jpg", [5, 6, 7, 8]);
        File.Delete(deletedPath);
        PhotoNavigator navigator = new([corruptPath, deletedPath]);

        PreviewPresentationResult corrupt = MainFormPresentation.LoadPreview(navigator.CurrentPath!);
        MainFormViewState first = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: true,
            copyInProgress: false,
            corrupt.Status);

        Assert.IsFalse(corrupt.IsAvailable);
        Assert.AreEqual("corrupt.jpg", first.FileName);
        Assert.AreEqual("1 of 2", first.Position);
        Assert.IsFalse(first.CanMovePrevious);
        Assert.IsTrue(first.CanMoveNext);
        Assert.IsTrue(first.CanCopy);
        StringAssert.StartsWith(first.Status, "Preview unavailable:");

        Assert.IsTrue(navigator.TryMoveNext(out string? currentPath));
        PreviewPresentationResult deleted = MainFormPresentation.LoadPreview(currentPath!);
        MainFormViewState second = MainFormPresentation.CreateViewState(
            navigator,
            destinationIsValid: true,
            copyInProgress: false,
            deleted.Status);

        Assert.IsFalse(deleted.IsAvailable);
        Assert.AreEqual("deleted.jpg", second.FileName);
        Assert.AreEqual("2 of 2", second.Position);
        Assert.IsTrue(second.CanMovePrevious);
        Assert.IsFalse(second.CanMoveNext);
        Assert.IsTrue(second.CanCopy);
        StringAssert.StartsWith(second.Status, "Preview unavailable:");
    }

    [TestMethod]
    public void StatusHelpers_ProduceVisibleSourceDestinationAndEmptyFolderErrors()
    {
        Assert.AreEqual(
            "No supported photos were found in this folder.",
            MainFormPresentation.SourceLoadedStatus(0));
        Assert.AreEqual(
            "Unable to load source folder: access denied",
            MainFormPresentation.SourceFolderErrorStatus("access denied"));
        Assert.AreEqual(
            "The selected destination folder is not available.",
            MainFormPresentation.DestinationStatus(destinationIsValid: false));
    }

    [TestMethod]
    public async Task CopyAsync_ReturnsExplicitStatusForCopyFailure()
    {
        MainFormViewState invalidDestination = MainFormPresentation.CreateViewState(
            new PhotoNavigator(["photo.jpg"]),
            destinationIsValid: false,
            copyInProgress: false,
            MainFormPresentation.InvalidDestinationCopyStatus);

        Assert.AreEqual(
            "Choose a valid destination folder before copying.",
            invalidDestination.Status);

        string failureStatus = await MainFormPresentation.CopyAsync(
            "photo.jpg",
            @"C:\destination",
            (_, _, _) => throw new IOException("disk full"));

        Assert.AreEqual(
            "Copy failed: disk full",
            failureStatus);
        Assert.AreEqual(
            "Copied as photo (1).jpg.",
            MainFormPresentation.CopySucceededStatus(
                Path.Combine(@"C:\destination", "photo (1).jpg")));
    }

    [TestMethod]
    public async Task CopyAsync_PassesCancellationTokenToPhotoCopier()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => MainFormPresentation.CopyAsync(
                "photo.jpg",
                @"C:\destination",
                (_, _, token) =>
                {
                    Assert.AreEqual(cancellation.Token, token);
                    token.ThrowIfCancellationRequested();
                    throw new AssertFailedException("Cancellation should have been observed.");
                },
                cancellation.Token));
    }

    [TestMethod]
    public void MainForm_CloseDuringCopyCancelsAndWaitsForCleanupBeforeClosing()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg");
        TaskCompletionSource copyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleanupCompleted = false;
        CancellationToken observedToken = default;

        MainForm form = new(
            async (source, destination, token) =>
            {
                observedToken = token;
                copyStarted.SetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    await allowCleanup.Task;
                    cleanupCompleted = true;
                }

                return new CopyResult(source, Path.Combine(destination, Path.GetFileName(source)));
            });

        SetCurrentCopy(form, sourcePath, destination.Path);
        Task copyTask = form.CopyCurrentPhotoAsync();
        WaitFor(() => copyStarted.Task.IsCompleted);
        Button copyButton = GetField<Button>(form, "copyButton");

        Assert.IsFalse(copyButton.Enabled);
        FormClosingEventArgs closing = new(CloseReason.UserClosing, cancel: false);
        form.HandleFormClosing(closing);

        Assert.IsTrue(closing.Cancel);
        Assert.IsTrue(observedToken.IsCancellationRequested);
        Assert.IsFalse(cleanupCompleted);
        Assert.IsNotNull(form.DeferredCloseTask);
        Assert.IsFalse(form.DeferredCloseTask.IsCompleted);
        Assert.IsFalse(form.IsDisposed);

        allowCleanup.SetResult();
        WaitFor(() => copyTask.IsCompleted && form.DeferredCloseTask.IsCompleted);
        copyTask.GetAwaiter().GetResult();
        form.DeferredCloseTask.GetAwaiter().GetResult();

        Assert.IsTrue(cleanupCompleted);
        Assert.IsTrue(form.IsDisposed);
    }

    [TestMethod]
    public void MainForm_DisposedDuringCopyDoesNotUpdateDisposedControls()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg");
        TaskCompletionSource<CopyResult> finishCopy =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        MainForm form = new((_, _, _) => finishCopy.Task);

        SetCurrentCopy(form, sourcePath, destination.Path);
        Task copyTask = form.CopyCurrentPhotoAsync();
        form.Dispose();
        finishCopy.SetResult(
            new CopyResult(sourcePath, Path.Combine(destination.Path, "photo.jpg")));

        WaitFor(() => copyTask.IsCompleted);
        copyTask.GetAwaiter().GetResult();

        Assert.IsTrue(form.IsDisposed);
    }

    [TestMethod]
    public async Task MainForm_InvalidDestinationRestoresBrowseAndNavigationControls()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string firstSourcePath = source.CreateFile("first.jpg");
        string secondSourcePath = source.CreateFile("second.jpg");
        bool copyWasCalled = false;
        using MainForm form = new((_, _, _) =>
        {
            copyWasCalled = true;
            throw new AssertFailedException("Copy should not run for a missing destination.");
        });

        SetCurrentCopy(
            form,
            [firstSourcePath, secondSourcePath],
            destination.Path);
        ApplyPresentation(form, MainFormPresentation.PreviewReadyStatus);

        Assert.IsTrue(GetField<Button>(form, "browseSourceButton").Enabled);
        Assert.IsTrue(GetField<Button>(form, "browseDestinationButton").Enabled);
        Assert.IsTrue(GetField<Button>(form, "nextButton").Enabled);

        Directory.Delete(destination.Path);

        await form.CopyCurrentPhotoAsync();

        Assert.IsFalse(copyWasCalled);
        Assert.AreEqual(
            MainFormPresentation.InvalidDestinationCopyStatus,
            GetField<Label>(form, "statusLabel").Text);
        Assert.IsTrue(GetField<Button>(form, "browseSourceButton").Enabled);
        Assert.IsTrue(GetField<Button>(form, "browseDestinationButton").Enabled);
        Assert.IsTrue(GetField<Button>(form, "nextButton").Enabled);
        Assert.IsFalse(GetField<Button>(form, "previousButton").Enabled);
        Assert.IsFalse(GetField<Button>(form, "copyButton").Enabled);
    }

    [TestMethod]
    public void MainForm_StatusLabelIsNamedPoliteLiveRegion()
    {
        using MainForm form = new();
        FieldInfo? statusLabelField = typeof(MainForm).GetField(
            "statusLabel",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(statusLabelField);
        Label statusLabel = (Label)statusLabelField.GetValue(form)!;
        Assert.AreEqual("Status", statusLabel.AccessibleName);
        Assert.AreEqual(AutomationLiveSetting.Polite, statusLabel.LiveSetting);
    }

    [TestMethod]
    public void UpdateStatusLabel_WhenTextChangesNotifiesLiveRegionOnce()
    {
        using StatusLabel statusLabel = new() { Text = "Old status" };
        List<(AccessibleEvents Event, int ChildId)> notifications = [];

        MainForm.UpdateStatusLabel(
            statusLabel,
            "New status",
            (accessibleEvent, childId) => notifications.Add((accessibleEvent, childId)));
        MainForm.UpdateStatusLabel(
            statusLabel,
            "New status",
            (accessibleEvent, childId) => notifications.Add((accessibleEvent, childId)));

        Assert.AreEqual("New status", statusLabel.Text);
        Assert.HasCount(1, notifications);
        Assert.AreEqual((AccessibleEvents)0x8019, notifications[0].Event);
        Assert.AreEqual(-1, notifications[0].ChildId);
    }

    private static void SetCurrentCopy(
        MainForm form,
        string sourcePath,
        string destinationDirectory)
    {
        FieldInfo? navigatorField = typeof(MainForm).GetField(
            "_navigator",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(navigatorField);
        navigatorField.SetValue(form, new PhotoNavigator([sourcePath]));
        GetField<TextBox>(form, "destinationPathTextBox").Text = destinationDirectory;
    }

    private static void SetCurrentCopy(
        MainForm form,
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory)
    {
        FieldInfo? navigatorField = typeof(MainForm).GetField(
            "_navigator",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(navigatorField);
        navigatorField.SetValue(form, new PhotoNavigator(sourcePaths));
        GetField<TextBox>(form, "destinationPathTextBox").Text = destinationDirectory;
    }

    private static void ApplyPresentation(MainForm form, string status)
    {
        MethodInfo? applyPresentation = typeof(MainForm).GetMethod(
            "ApplyPresentation",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(applyPresentation);
        applyPresentation.Invoke(form, [status]);
    }

    private static T GetField<T>(MainForm form, string fieldName)
        where T : class
    {
        FieldInfo? field = typeof(MainForm).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(field);
        return (T)field.GetValue(form)!;
    }

    private static void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the asynchronous form operation.");
            }

            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
}
