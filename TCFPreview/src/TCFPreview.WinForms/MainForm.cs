using TCFPreview.Core;

namespace TCFPreview.WinForms;

public partial class MainForm : Form
{
    private readonly Func<string, string, CancellationToken, Task<CopyResult>> _copyAsync;
    private PhotoNavigator _navigator = new([]);
    private CancellationTokenSource? _activeCopyCancellation;
    private Task? _activeCopyTask;
    private Task? _deferredCloseTask;
    private bool _copyInProgress;
    private bool _closePending;
    private bool _closingAfterCopy;

    public MainForm()
        : this(PhotoCopier.CopyAsync)
    {
    }

    internal MainForm(Func<string, string, CancellationToken, Task<CopyResult>> copyAsync)
    {
        ArgumentNullException.ThrowIfNull(copyAsync);
        _copyAsync = copyAsync;
        InitializeComponent();
        UpdateView();
    }

    private void BrowseSourceButton_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Choose a folder containing photos",
            ShowNewFolderButton = false,
            SelectedPath = sourcePathTextBox.Text,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> photos = PhotoDiscovery.Discover(dialog.SelectedPath);
            _navigator = new PhotoNavigator(photos);
            sourcePathTextBox.Text = dialog.SelectedPath;
            UpdateView(MainFormPresentation.SourceLoadedStatus(photos.Count));
        }
        catch (UnauthorizedAccessException ex)
        {
            ShowSourceError(ex.Message);
        }
        catch (IOException ex)
        {
            ShowSourceError(ex.Message);
        }
    }

    private void BrowseDestinationButton_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Choose the folder where photos will be copied",
            ShowNewFolderButton = true,
            SelectedPath = destinationPathTextBox.Text,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        destinationPathTextBox.Text = dialog.SelectedPath;
        bool destinationIsValid = Directory.Exists(dialog.SelectedPath);
        ApplyPresentation(MainFormPresentation.DestinationStatus(destinationIsValid));
    }

    private void PreviousButton_Click(object? sender, EventArgs e)
    {
        if (_navigator.TryMovePrevious(out _))
        {
            UpdateView();
        }
    }

    private void NextButton_Click(object? sender, EventArgs e)
    {
        if (_navigator.TryMoveNext(out _))
        {
            UpdateView();
        }
    }

    private async void CopyButton_Click(object? sender, EventArgs e)
    {
        await CopyCurrentPhotoAsync();
    }

    internal Task CopyCurrentPhotoAsync()
    {
        if (_activeCopyTask is { IsCompleted: false })
        {
            return _activeCopyTask;
        }

        CancellationTokenSource cancellation = new();
        _activeCopyCancellation = cancellation;
        _copyInProgress = true;
        _activeCopyTask = CopyCurrentPhotoCoreAsync(cancellation);
        return _activeCopyTask;
    }

    private async Task CopyCurrentPhotoCoreAsync(CancellationTokenSource cancellation)
    {
        string? sourcePath = _navigator.CurrentPath;
        string destinationDirectory = destinationPathTextBox.Text;

        if (sourcePath is null)
        {
            CompleteCopyOperation(cancellation);
            ApplyPresentation(MainFormPresentation.NoCurrentPhotoCopyStatus);
            return;
        }

        if (!Directory.Exists(destinationDirectory))
        {
            CompleteCopyOperation(cancellation);
            ApplyPresentation(MainFormPresentation.InvalidDestinationCopyStatus);
            return;
        }

        ApplyPresentation(MainFormPresentation.CopyingStatus);

        try
        {
            string copyStatus = await MainFormPresentation.CopyAsync(
                sourcePath,
                destinationDirectory,
                _copyAsync,
                cancellation.Token);

            if (!_closePending && !IsDisposed && !Disposing)
            {
                ApplyPresentation(copyStatus);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing the form owns cancellation; no status update should race disposal.
        }
        finally
        {
            CompleteCopyOperation(cancellation);

            if (!_closePending && !IsDisposed && !Disposing)
            {
                ApplyPresentation(statusLabel.Text);
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        HandleFormClosing(e);
    }

    internal void HandleFormClosing(FormClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_closingAfterCopy || _activeCopyTask is not { IsCompleted: false } activeCopyTask)
        {
            return;
        }

        e.Cancel = true;
        _closePending = true;
        _activeCopyCancellation?.Cancel();
        _deferredCloseTask ??= CloseAfterCopyAsync(activeCopyTask);
    }

    internal Task? DeferredCloseTask => _deferredCloseTask;

    private async Task CloseAfterCopyAsync(Task activeCopyTask)
    {
        try
        {
            await activeCopyTask;
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _closingAfterCopy = true;
                Close();
            }
        }
    }

    private void CompleteCopyOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_activeCopyCancellation, cancellation))
        {
            _activeCopyCancellation = null;
            _copyInProgress = false;
        }

        cancellation.Dispose();
    }

    private void UpdateView(string? status = null)
    {
        string? currentPath = _navigator.CurrentPath;

        if (currentPath is null)
        {
            ReplacePreview(null);
            ApplyPresentation(status ?? MainFormPresentation.InitialStatus);
        }
        else
        {
            PreviewPresentationResult preview = MainFormPresentation.LoadPreview(currentPath);
            ReplacePreview(preview.Image);
            ApplyPresentation(preview.IsAvailable ? status ?? preview.Status : preview.Status);
        }
    }

    private void ReplacePreview(Image? replacement)
    {
        Image? previous = previewPictureBox.Image;
        previewPictureBox.Image = replacement;
        previous?.Dispose();
    }

    private void ApplyPresentation(string status)
    {
        MainFormViewState state = MainFormPresentation.CreateViewState(
            _navigator,
            Directory.Exists(destinationPathTextBox.Text),
            _copyInProgress,
            status);

        filenameLabel.Text = state.FileName;
        positionLabel.Text = state.Position;
        UpdateStatusLabel(statusLabel, state.Status);
        previousButton.Enabled = state.CanMovePrevious;
        nextButton.Enabled = state.CanMoveNext;
        copyButton.Enabled = state.CanCopy;
        browseSourceButton.Enabled = state.CanBrowse;
        browseDestinationButton.Enabled = state.CanBrowse;
    }

    internal static void UpdateStatusLabel(
        StatusLabel label,
        string status,
        Action<AccessibleEvents, int>? notifyAccessibilityClients = null)
    {
        if (label.Text == status)
        {
            return;
        }

        label.Text = status;
        if (notifyAccessibilityClients is null)
        {
            label.NotifyLiveRegionChanged();
        }
        else
        {
            notifyAccessibilityClients(StatusLabel.LiveRegionChangedEvent, -1);
        }
    }

    private void ShowSourceError(string message)
    {
        ApplyPresentation(MainFormPresentation.SourceFolderErrorStatus(message));
    }
}

internal sealed class StatusLabel : Label
{
    internal const AccessibleEvents LiveRegionChangedEvent = (AccessibleEvents)0x8019;

    internal void NotifyLiveRegionChanged()
    {
        AccessibilityNotifyClients(LiveRegionChangedEvent, -1);
    }
}
