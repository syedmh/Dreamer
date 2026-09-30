using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Husaynia.BaselineCapture;

internal interface IPromotionArtifactConstructionObserver
{
    void AfterCandidateCreateBeforeValidation(string siblingName);

    void AfterJournalCreateBeforeValidation(string siblingName);

    void CandidateCreated(string siblingName);

    void BeforeDiagnosticFlush(string siblingName);

    void BeforeConstructionCleanup(string siblingName);
}

internal sealed class PromotionArtifactConstructionException(
    string reason,
    string logicalName,
    Exception operationException,
    Exception cleanupException)
    : IOException(
        reason,
        new AggregateException(
            operationException,
            cleanupException))
{
    public string LogicalName { get; } = logicalName;

    public Exception OperationException { get; } = operationException;
}

public sealed class PhysicalPromotionFileSystem : IPromotionFileSystem
{
    private readonly IPromotionFileSystemTrustPolicy _trustPolicy;
    private readonly IPromotionArtifactConstructionObserver? _observer;

    public PhysicalPromotionFileSystem(
        IPromotionFileSystemTrustPolicy? trustPolicy = null)
        : this(trustPolicy, observer: null)
    {
    }

    internal PhysicalPromotionFileSystem(
        IPromotionFileSystemTrustPolicy? trustPolicy,
        IPromotionArtifactConstructionObserver? observer)
    {
        _trustPolicy = trustPolicy
            ?? new PhysicalPromotionFileSystemTrustPolicy();
        _observer = observer;
    }

    public IPromotionFileSystemLease OpenLease(
        string sourceRoot,
        string destinationRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "promotion-handle-relative-filesystem-unavailable");
        }

        try
        {
            return new WindowsPromotionFileSystemLease(
                sourceRoot,
                destinationRoot,
                _trustPolicy,
                _observer);
        }
        catch (PromotionLockUnavailableException)
        {
            throw;
        }
        catch (PromotionFileSystemSafetyException)
        {
            throw;
        }
        catch (Win32Exception exception)
        {
            throw new PromotionFileSystemSafetyException(
                $"promotion-filesystem-open-refused:win32-{exception.NativeErrorCode}");
        }
    }

    public IReadOnlyList<BaselineFileManifestEntry> EnumerateTree(
        string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "promotion-handle-relative-filesystem-unavailable");
        }

        using var handle =
            WindowsPromotionFileSystem.OpenAnchoredDirectory(
                Path.GetFullPath(root),
                finalRequireCreateTrust: true,
                _trustPolicy);
        return WindowsPromotionFileSystem.CaptureTree(
            handle,
            _trustPolicy).Files;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPromotionFileSystemLease
    : IPromotionFileSystemLease
{
    private readonly string _parentPath;
    private readonly SafeFileHandle _sourceHandle;
    private readonly SafeFileHandle _parentHandle;
    private readonly SafeFileHandle _lockHandle;
    private readonly PromotionArtifactIdentity _sourceIdentity;
    private readonly PromotionArtifactIdentity _parentIdentity;
    private readonly IPromotionFileSystemTrustPolicy _trustPolicy;
    private readonly IPromotionArtifactConstructionObserver? _observer;
    private readonly byte[] _privateDirectorySecurity;
    private readonly byte[] _privateFileSecurity;
    private bool _disposed;

    public WindowsPromotionFileSystemLease(
        string sourceRoot,
        string destinationRoot,
        IPromotionFileSystemTrustPolicy trustPolicy,
        IPromotionArtifactConstructionObserver? observer)
    {
        SourceRoot = Path.GetFullPath(sourceRoot);
        DestinationRoot = Path.GetFullPath(destinationRoot);
        _parentPath = Path.GetDirectoryName(DestinationRoot)
            ?? throw new PromotionFileSystemSafetyException(
                "promotion-destination-parent-missing");
        DestinationName = Path.GetFileName(DestinationRoot);
        ValidateSiblingName(DestinationName);
        _trustPolicy = trustPolicy;
        _observer = observer;
        _privateDirectorySecurity =
            PhysicalPromotionFileSystemTrustPolicy
                .CreatePrivateSecurityDescriptor(
                    isDirectory: true);
        _privateFileSecurity =
            PhysicalPromotionFileSystemTrustPolicy
                .CreatePrivateSecurityDescriptor(
                    isDirectory: false);

        _sourceHandle =
            WindowsPromotionFileSystem.OpenAnchoredDirectory(
                SourceRoot,
                finalRequireCreateTrust: true,
                _trustPolicy);
        try
        {
            _parentHandle =
                WindowsPromotionFileSystem.OpenAnchoredDirectory(
                    _parentPath,
                    finalRequireCreateTrust: true,
                    _trustPolicy);
        }
        catch
        {
            _sourceHandle.Dispose();
            throw;
        }

        SafeFileHandle? lockHandle = null;
        try
        {
            var sourceInfo =
                WindowsPromotionFileSystem.GetVerifiedInformation(
                    _sourceHandle,
                    requireDirectory: true,
                    requireSingleLinkFile: false);
            var parentInfo =
                WindowsPromotionFileSystem.GetVerifiedInformation(
                    _parentHandle,
                    requireDirectory: true,
                    requireSingleLinkFile: false);
            _sourceIdentity = sourceInfo.Identity;
            _parentIdentity = parentInfo.Identity;
            if (_sourceIdentity.VolumeSerialNumber
                != _parentIdentity.VolumeSerialNumber)
            {
                throw new PromotionFileSystemSafetyException(
                    "promotion-same-volume-distinct-paths-required");
            }

            EnsureSourceHandleIdentity();
            EnsureParentHandleIdentity();
            var lockName = $"{DestinationName}.promotion.lock";
            try
            {
                lockHandle =
                    WindowsPromotionFileSystem.OpenRelative(
                        _parentHandle,
                        lockName,
                        WindowsPromotionFileSystem.FileReadData
                        | WindowsPromotionFileSystem.FileWriteData
                        | WindowsPromotionFileSystem.FileReadAttributes
                        | WindowsPromotionFileSystem.Synchronize,
                        WindowsPromotionFileSystem.FileOpenIf,
                        WindowsPromotionFileSystem.FileNonDirectoryFile,
                        shareAccess: 0,
                        fileAttributes:
                        WindowsPromotionFileSystem.FileAttributeNormal,
                        securityDescriptor:
                        _privateFileSecurity);
            }
            catch (Win32Exception exception)
                when (exception.NativeErrorCode
                      == WindowsPromotionFileSystem.ErrorSharingViolation)
            {
                throw new PromotionLockUnavailableException(
                    "promotion-lock-held");
            }

            WindowsPromotionFileSystem.GetVerifiedInformation(
                lockHandle,
                requireDirectory: false,
                requireSingleLinkFile: true);
            WindowsPromotionFileSystem.EnsureTrustedSecurity(
                _trustPolicy,
                lockHandle,
                requireCreateTrust: true,
                rejectUnsafeInheritance: false);
            EnsureParentHandleIdentity();
            _lockHandle = lockHandle;
            lockHandle = null;
        }
        catch
        {
            lockHandle?.Dispose();
            _sourceHandle.Dispose();
            _parentHandle.Dispose();
            throw;
        }
    }

    public string SourceRoot { get; }

    public string DestinationRoot { get; }

    public string DestinationName { get; }

    public IReadOnlyList<string> ListPromotionResidue()
    {
        ThrowIfDisposed();
        EnsureParentHandleIdentity();
        var residue = WindowsPromotionFileSystem
            .ReadDirectoryNames(_parentHandle)
            .Where(IsPromotionResidue)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (residue.Length == 0)
        {
            EnsureDestinationRootTrustedIfPresent();
        }

        EnsureParentHandleIdentity();
        return residue;
    }

    public PromotionTreeSnapshot CaptureSourceTree()
    {
        ThrowIfDisposed();
        EnsureSourceHandleIdentity();
        var snapshot =
            WindowsPromotionFileSystem.CaptureTree(
                _sourceHandle,
                _trustPolicy);
        if (snapshot.RootIdentity != _sourceIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-source-identity-changed");
        }

        EnsureSourceHandleIdentity();
        return snapshot;
    }

    public PromotionTreeSnapshot? CaptureDestinationTree()
    {
        ThrowIfDisposed();
        EnsureParentHandleIdentity();
        using var handle = TryOpenSiblingDirectory(DestinationName);
        if (handle is null)
        {
            return null;
        }

        var snapshot = WindowsPromotionFileSystem.CaptureTree(
            handle,
            _trustPolicy);
        EnsureParentHandleIdentity();
        return snapshot;
    }

    public PromotionTreeSnapshot CaptureSiblingTree(string siblingName)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        using var handle =
            WindowsPromotionFileSystem.OpenRelativeDirectory(
                _parentHandle,
                siblingName,
                includeDeleteAccess: false);
        var snapshot = WindowsPromotionFileSystem.CaptureTree(
            handle,
            _trustPolicy);
        EnsureParentHandleIdentity();
        return snapshot;
    }

    public PromotionArtifactIdentity? TryGetSiblingIdentity(
        string siblingName)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        using var handle = WindowsPromotionFileSystem.TryOpenRelative(
            _parentHandle,
            siblingName,
            WindowsPromotionFileSystem.FileReadAttributes
            | WindowsPromotionFileSystem.Synchronize,
            WindowsPromotionFileSystem.FileOpen,
            WindowsPromotionFileSystem.FileOpenReparsePoint,
            WindowsPromotionFileSystem.AllShareAccess);
        if (handle is null)
        {
            return null;
        }

        var information =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                handle,
                requireDirectory: null,
                requireSingleLinkFile: true);
        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            handle,
            requireCreateTrust: true,
            rejectUnsafeInheritance: information.IsDirectory);
        EnsureParentHandleIdentity();
        return information.Identity;
    }

    public async Task<PromotionTreeSnapshot>
        CopySourceToSiblingCreateNewAsync(
            string siblingName,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        EnsureSourceHandleIdentity();
        using var destination =
            WindowsPromotionFileSystem.CreateRelativeDirectory(
                _parentHandle,
                siblingName,
                _privateDirectorySecurity,
                _observer is null
                    ? null
                    : _observer.AfterCandidateCreateBeforeValidation,
                _observer is null
                    ? null
                    : _observer.BeforeConstructionCleanup,
                "promotion-candidate-construction-residue",
                siblingName);
        try
        {
            WindowsPromotionFileSystem.GetVerifiedInformation(
                destination,
                requireDirectory: true,
                requireSingleLinkFile: false);
            WindowsPromotionFileSystem.EnsureTrustedSecurity(
                _trustPolicy,
                destination,
                requireCreateTrust: true,
                rejectUnsafeInheritance: true);
            _observer?.CandidateCreated(siblingName);
            await WindowsPromotionFileSystem.CopyTreeAsync(
                _sourceHandle,
                destination,
                _trustPolicy,
                _privateDirectorySecurity,
                _privateFileSecurity,
                siblingName,
                cancellationToken);
            var snapshot =
                WindowsPromotionFileSystem.CaptureTree(
                    destination,
                    _trustPolicy);
            EnsureSourceHandleIdentity();
            EnsureParentHandleIdentity();
            return snapshot;
        }
        catch (Exception operationException)
        {
            try
            {
                _observer?.BeforeConstructionCleanup(siblingName);
                WindowsPromotionFileSystem.DeleteOwnedTreeContents(
                    destination);
                WindowsPromotionFileSystem.DeleteByHandle(destination);
                destination.Dispose();
                EnsureSiblingAbsent(siblingName);
                EnsureParentHandleIdentity();
            }
            catch (Exception cleanupException)
            {
                throw new PromotionArtifactConstructionException(
                    "promotion-candidate-construction-residue",
                    siblingName,
                    operationException,
                    cleanupException);
            }

            if (operationException
                is PromotionArtifactConstructionException
                    constructionException)
            {
                ExceptionDispatchInfo.Capture(
                        constructionException.OperationException)
                    .Throw();
            }

            throw;
        }
    }

    public PromotionArtifactIdentity CreateDiagnosticFile(
        string siblingName,
        ReadOnlySpan<byte> content)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        using var handle =
            WindowsPromotionFileSystem.CreateRelativeFile(
                _parentHandle,
                siblingName,
                _privateFileSecurity,
                _observer is null
                    ? null
                    : _observer.AfterJournalCreateBeforeValidation,
                _observer is null
                    ? null
                    : _observer.BeforeConstructionCleanup,
                "promotion-journal-construction-residue",
                siblingName);
        try
        {
            var information =
                WindowsPromotionFileSystem.GetVerifiedInformation(
                    handle,
                    requireDirectory: false,
                    requireSingleLinkFile: true);
            WindowsPromotionFileSystem.EnsureTrustedSecurity(
                _trustPolicy,
                handle,
                requireCreateTrust: true,
                rejectUnsafeInheritance: false);
            RandomAccess.Write(handle, content, fileOffset: 0);
            _observer?.BeforeDiagnosticFlush(siblingName);
            RandomAccess.FlushToDisk(handle);
            EnsureParentHandleIdentity();
            return information.Identity;
        }
        catch (Exception operationException)
        {
            try
            {
                _observer?.BeforeConstructionCleanup(siblingName);
                WindowsPromotionFileSystem.DeleteByHandle(handle);
                handle.Dispose();
                EnsureSiblingAbsent(siblingName);
                EnsureParentHandleIdentity();
            }
            catch (Exception cleanupException)
            {
                throw new PromotionArtifactConstructionException(
                    "promotion-journal-construction-residue",
                    siblingName,
                    operationException,
                    cleanupException);
            }

            throw;
        }
    }

    public void MoveSibling(
        string sourceSiblingName,
        string destinationSiblingName,
        PromotionArtifactIdentity expectedSourceIdentity)
    {
        ThrowIfDisposed();
        ValidateSiblingName(sourceSiblingName);
        ValidateSiblingName(destinationSiblingName);
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                MoveSiblingOnce(
                    sourceSiblingName,
                    destinationSiblingName,
                    expectedSourceIdentity);
                return;
            }
            catch (Win32Exception exception)
                when (exception.NativeErrorCode is 5 or 32
                      && attempt < 5)
            {
                lastFailure = exception;
                Thread.Sleep(attempt * 50);
            }
        }

        throw lastFailure
            ?? new IOException("promotion-directory-move-failed");
    }

    public void DeleteTreeIfExact(
        string siblingName,
        PromotionTreeSnapshot expectedSnapshot)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        using var root =
            WindowsPromotionFileSystem.TryOpenRelativeDirectory(
                _parentHandle,
                siblingName,
                includeDeleteAccess: true);
        if (root is null)
        {
            return;
        }

        var actual = WindowsPromotionFileSystem.CaptureTree(
            root,
            _trustPolicy);
        if (!WindowsPromotionFileSystem.SnapshotsEqual(
                expectedSnapshot,
                actual))
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-cleanup-identity-changed");
        }

        WindowsPromotionFileSystem.DeleteTreeContents(root);
        WindowsPromotionFileSystem.DeleteByHandle(root);
        root.Dispose();
        using var remaining =
            WindowsPromotionFileSystem.TryOpenRelative(
                _parentHandle,
                siblingName,
                WindowsPromotionFileSystem.FileReadAttributes
                | WindowsPromotionFileSystem.Synchronize,
                WindowsPromotionFileSystem.FileOpen,
                WindowsPromotionFileSystem.FileOpenReparsePoint,
                WindowsPromotionFileSystem.AllShareAccess);
        if (remaining is not null)
        {
            throw new IOException(
                "promotion-cleanup-incomplete");
        }

        EnsureParentHandleIdentity();
    }

    public void DeleteOwnedTreeIfRootIdentity(
        string siblingName,
        PromotionArtifactIdentity expectedRootIdentity)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        using var root =
            WindowsPromotionFileSystem.TryOpenRelativeDirectory(
                _parentHandle,
                siblingName,
                includeDeleteAccess: true);
        if (root is null)
        {
            return;
        }

        var rootInformation =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                root,
                requireDirectory: true,
                requireSingleLinkFile: false);
        if (rootInformation.Identity != expectedRootIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-cleanup-identity-changed");
        }

        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            root,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
        WindowsPromotionFileSystem.DeleteOwnedTreeContents(root);
        WindowsPromotionFileSystem.DeleteByHandle(root);
        root.Dispose();
        EnsureParentHandleIdentity();
    }

    public void DeleteFileIfIdentity(
        string siblingName,
        PromotionArtifactIdentity expectedIdentity)
    {
        ThrowIfDisposed();
        ValidateSiblingName(siblingName);
        EnsureParentHandleIdentity();
        using var handle = WindowsPromotionFileSystem.TryOpenRelative(
            _parentHandle,
            siblingName,
            WindowsPromotionFileSystem.Delete
            | WindowsPromotionFileSystem.FileReadAttributes
            | WindowsPromotionFileSystem.Synchronize,
            WindowsPromotionFileSystem.FileOpen,
            WindowsPromotionFileSystem.FileNonDirectoryFile
            | WindowsPromotionFileSystem.FileOpenReparsePoint,
            WindowsPromotionFileSystem.AllShareAccess);
        if (handle is null)
        {
            return;
        }

        var information =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                handle,
                requireDirectory: false,
                requireSingleLinkFile: false);
        if (information.Identity != expectedIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-cleanup-identity-changed");
        }

        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            handle,
            requireCreateTrust: true,
            rejectUnsafeInheritance: false);
        WindowsPromotionFileSystem.DeleteByHandle(handle);
        EnsureParentHandleIdentity();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lockHandle.Dispose();
        _parentHandle.Dispose();
        _sourceHandle.Dispose();
        _disposed = true;
    }

    private void EnsureSiblingAbsent(string siblingName)
    {
        using var remaining =
            WindowsPromotionFileSystem.TryOpenRelative(
                _parentHandle,
                siblingName,
                WindowsPromotionFileSystem.FileReadAttributes
                | WindowsPromotionFileSystem.Synchronize,
                WindowsPromotionFileSystem.FileOpen,
                WindowsPromotionFileSystem.FileOpenReparsePoint,
                WindowsPromotionFileSystem.AllShareAccess);
        if (remaining is not null)
        {
            throw new IOException(
                "promotion-construction-cleanup-incomplete");
        }
    }

    private SafeFileHandle? TryOpenSiblingDirectory(string siblingName) =>
        WindowsPromotionFileSystem.TryOpenRelativeDirectory(
            _parentHandle,
            siblingName,
            includeDeleteAccess: false);

    private void EnsureDestinationRootTrustedIfPresent()
    {
        using var destination =
            WindowsPromotionFileSystem.TryOpenRelative(
                _parentHandle,
                DestinationName,
                WindowsPromotionFileSystem.FileReadAttributes
                | WindowsPromotionFileSystem.Synchronize,
                WindowsPromotionFileSystem.FileOpen,
                WindowsPromotionFileSystem.FileOpenReparsePoint,
                WindowsPromotionFileSystem.AllShareAccess);
        if (destination is null)
        {
            return;
        }

        WindowsPromotionFileSystem.GetVerifiedInformation(
            destination,
            requireDirectory: true,
            requireSingleLinkFile: false);
        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            destination,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
    }

    private void MoveSiblingOnce(
        string sourceSiblingName,
        string destinationSiblingName,
        PromotionArtifactIdentity expectedSourceIdentity)
    {
        EnsureParentHandleIdentity();
        using var source =
            WindowsPromotionFileSystem.OpenRelativeDirectory(
                _parentHandle,
                sourceSiblingName,
                includeDeleteAccess: true);
        var information =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                source,
                requireDirectory: true,
                requireSingleLinkFile: false);
        if (information.Identity != expectedSourceIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-move-source-identity-changed");
        }

        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            source,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
        using (var existing =
               WindowsPromotionFileSystem.TryOpenRelative(
                   _parentHandle,
                   destinationSiblingName,
                   WindowsPromotionFileSystem.FileReadAttributes
                   | WindowsPromotionFileSystem.Synchronize,
                   WindowsPromotionFileSystem.FileOpen,
                   WindowsPromotionFileSystem.FileOpenReparsePoint,
                   WindowsPromotionFileSystem.AllShareAccess))
        {
            if (existing is not null)
            {
                throw new PromotionFileSystemSafetyException(
                    "promotion-move-destination-exists");
            }
        }

        WindowsPromotionFileSystem.RenameRelative(
            source,
            _parentHandle,
            destinationSiblingName);
        var after = WindowsPromotionFileSystem.GetVerifiedInformation(
            source,
            requireDirectory: true,
            requireSingleLinkFile: false);
        if (after.Identity != expectedSourceIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-move-source-identity-changed");
        }

        EnsureParentHandleIdentity();
    }

    private bool IsPromotionResidue(string name) =>
        name.StartsWith(
            $"{DestinationName}.prior-",
            StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(
            $"{DestinationName}.candidate-",
            StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(
            $"{DestinationName}.failed-",
            StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(
            $"{DestinationName}.transaction-",
            StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith(
                $".{DestinationName}.transaction-",
                StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(
                ".tmp",
                StringComparison.OrdinalIgnoreCase));

    private void EnsureSourceHandleIdentity()
    {
        var information =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                _sourceHandle,
                requireDirectory: true,
                requireSingleLinkFile: false);
        if (information.Identity != _sourceIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-source-identity-changed");
        }

        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            _sourceHandle,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
    }

    private void EnsureParentHandleIdentity()
    {
        var information =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                _parentHandle,
                requireDirectory: true,
                requireSingleLinkFile: false);
        if (information.Identity != _parentIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-parent-identity-changed");
        }

        WindowsPromotionFileSystem.EnsureTrustedSecurity(
            _trustPolicy,
            _parentHandle,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
    }

    private static void ValidateSiblingName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || Path.IsPathRooted(name)
            || !Path.GetFileName(name).Equals(
                name,
                StringComparison.Ordinal)
            || name is "." or "..")
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-sibling-name-invalid");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPromotionFileSystem
{
    internal const int MaximumPromotionTreeDepth = 64;
    internal const uint Delete = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint GenericRead = 0x80000000;
    internal const uint FileReadData = 0x00000001;
    internal const uint FileWriteData = 0x00000002;
    internal const uint FileAppendData = 0x00000004;
    internal const uint FileListDirectory = 0x00000001;
    internal const uint FileTraverse = 0x00000020;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint Synchronize = 0x00100000;

    internal const uint FileOpen = 0x00000001;
    internal const uint FileCreate = 0x00000002;
    internal const uint FileOpenIf = 0x00000003;

    internal const uint FileDirectoryFile = 0x00000001;
    internal const uint FileSynchronousIoNonAlert = 0x00000020;
    internal const uint FileNonDirectoryFile = 0x00000040;
    internal const uint FileOpenReparsePoint = 0x00200000;

    internal const uint FileAttributeReadOnly = 0x00000001;
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileAttributeReparsePoint = 0x00000400;

    internal const uint AllShareAccess = 0x00000007;
    internal const int ErrorFileNotFound = 2;
    internal const int ErrorPathNotFound = 3;
    internal const int ErrorSharingViolation = 32;
    private const int ErrorCantAccessFile = 1920;
    private const int ErrorNotAReparsePoint = 4390;

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint ObjDontReparse = 0x00001000;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int FileNamesInformation = 12;
    private const int FileDispositionInfo = 4;
    private const int FileDispositionInfoEx = 21;
    private const int FileDispositionInformation = 13;
    private const int FileRenameInformation = 10;
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const uint FileDispositionFlagPosixSemantics = 0x00000002;
    private const uint FileDispositionFlagIgnoreReadonlyAttribute = 0x00000010;

    internal static SafeFileHandle OpenAnchoredDirectory(
        string path,
        bool finalRequireCreateTrust,
        IPromotionFileSystemTrustPolicy trustPolicy)
    {
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-path-root-missing");
        }

        var relative = fullPath[rootPath.Length..];
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = OpenVolumeRoot(rootPath);
        try
        {
            var rootInformation = GetVerifiedInformation(
                current,
                requireDirectory: true,
                requireSingleLinkFile: false);
            EnsureTrustedSecurity(
                trustPolicy,
                current,
                requireCreateTrust:
                segments.Length == 0 && finalRequireCreateTrust,
                rejectUnsafeInheritance:
                segments.Length == 0 && finalRequireCreateTrust);
            for (var index = 0; index < segments.Length; index++)
            {
                var next = OpenRelativeDirectory(
                    current,
                    segments[index],
                    includeDeleteAccess: false);
                try
                {
                    var information = GetVerifiedInformation(
                        next,
                        requireDirectory: true,
                        requireSingleLinkFile: false);
                    if (information.Identity.VolumeSerialNumber
                        != rootInformation.Identity.VolumeSerialNumber)
                    {
                        throw new PromotionFileSystemSafetyException(
                            "promotion-path-volume-changed");
                    }

                    var isFinal = index == segments.Length - 1;
                    EnsureTrustedSecurity(
                        trustPolicy,
                        next,
                        requireCreateTrust:
                        isFinal && finalRequireCreateTrust,
                        rejectUnsafeInheritance:
                        isFinal && finalRequireCreateTrust);
                }
                catch
                {
                    next.Dispose();
                    throw;
                }

                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    internal static void EnsureTrustedSecurity(
        IPromotionFileSystemTrustPolicy trustPolicy,
        SafeFileHandle handle,
        bool requireCreateTrust,
        bool rejectUnsafeInheritance)
    {
        try
        {
            trustPolicy.EnsureTrustedHandle(
                handle,
                requireCreateTrust,
                rejectUnsafeInheritance);
        }
        catch (PromotionFileSystemSafetyException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or UnauthorizedAccessException
                or Win32Exception)
        {
            throw new PromotionFileSystemSafetyException(
                exception.Message);
        }
    }

    private static SafeFileHandle OpenVolumeRoot(string rootPath)
    {
        var access = GenericRead
                     | ReadControl
                     | FileListDirectory
                     | FileTraverse
                     | FileReadAttributes
                     | Synchronize;
        var handle = CreateFileW(
            rootPath,
            access,
            AllShareAccess,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        GetVerifiedInformation(
            handle,
            requireDirectory: true,
            requireSingleLinkFile: false);
        return handle;
    }

    internal static SafeFileHandle OpenRelativeDirectory(
        SafeFileHandle parent,
        string name,
        bool includeDeleteAccess) =>
        TryOpenRelativeDirectory(parent, name, includeDeleteAccess)
        ?? throw new Win32Exception(ErrorPathNotFound);

    internal static SafeFileHandle? TryOpenRelativeDirectory(
        SafeFileHandle parent,
        string name,
        bool includeDeleteAccess)
    {
        var access = FileListDirectory
                     | FileTraverse
                     | FileReadAttributes
                     | Synchronize;
        if (includeDeleteAccess)
        {
            access |= Delete;
        }

        return TryOpenRelative(
            parent,
            name,
            access,
            FileOpen,
            FileDirectoryFile | FileOpenReparsePoint,
            AllShareAccess);
    }

    internal static SafeFileHandle CreateRelativeDirectory(
        SafeFileHandle parent,
        string name,
        byte[] securityDescriptor,
        Action<string>? afterCreateBeforeValidation = null,
        Action<string>? beforeConstructionCleanup = null,
        string residueReason =
            "promotion-directory-construction-residue",
        string? residueLogicalName = null) =>
        CreateRelativeOwned(
            parent,
            name,
            FileListDirectory
            | FileTraverse
            | FileReadAttributes
            | FileWriteAttributes
            | Delete
            | Synchronize,
            FileCreate,
            FileDirectoryFile,
            AllShareAccess,
            FileAttributeDirectory,
            securityDescriptor,
            requireDirectory: true,
            requireSingleLinkFile: false,
            afterCreateBeforeValidation,
            beforeConstructionCleanup,
            residueReason,
            residueLogicalName ?? name);

    internal static SafeFileHandle CreateRelativeFile(
        SafeFileHandle parent,
        string name,
        byte[] securityDescriptor,
        Action<string>? afterCreateBeforeValidation = null,
        Action<string>? beforeConstructionCleanup = null,
        string residueReason =
            "promotion-file-construction-residue",
        string? residueLogicalName = null) =>
        CreateRelativeOwned(
            parent,
            name,
            FileReadData
            | FileWriteData
            | FileAppendData
            | FileReadAttributes
            | FileWriteAttributes
            | Delete
            | Synchronize,
            FileCreate,
            FileNonDirectoryFile,
            AllShareAccess,
            FileAttributeNormal,
            securityDescriptor,
            requireDirectory: false,
            requireSingleLinkFile: true,
            afterCreateBeforeValidation,
            beforeConstructionCleanup,
            residueReason,
            residueLogicalName ?? name);

    internal static SafeFileHandle OpenRelative(
        SafeFileHandle root,
        string name,
        uint desiredAccess,
        uint createDisposition,
        uint createOptions,
        uint shareAccess,
        uint fileAttributes = 0,
        byte[]? securityDescriptor = null)
    {
        var handle = TryOpenRelative(
            root,
            name,
            desiredAccess,
            createDisposition,
            createOptions,
            shareAccess,
            fileAttributes,
            throwIfMissing: true,
            securityDescriptor: securityDescriptor);
        return handle
            ?? throw new Win32Exception(ErrorPathNotFound);
    }

    internal static SafeFileHandle? TryOpenRelative(
        SafeFileHandle root,
        string name,
        uint desiredAccess,
        uint createDisposition,
        uint createOptions,
        uint shareAccess,
        uint fileAttributes = 0) =>
        TryOpenRelative(
            root,
            name,
            desiredAccess,
            createDisposition,
            createOptions,
            shareAccess,
            fileAttributes,
            throwIfMissing: false);

    internal static FileInformation GetVerifiedInformation(
        SafeFileHandle handle,
        bool? requireDirectory,
        bool requireSingleLinkFile)
    {
        var information = GetRawInformation(handle);

        if ((information.FileAttributes
             & FileAttributeReparsePoint) != 0)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-reparse-point");
        }

        var isDirectory =
            (information.FileAttributes & FileAttributeDirectory) != 0;
        if (requireDirectory is not null
            && isDirectory != requireDirectory.Value)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-artifact-shape-invalid");
        }

        if (requireSingleLinkFile
            && !isDirectory
            && information.NumberOfLinks != 1)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-hard-link-refused");
        }

        return new FileInformation(
            new PromotionArtifactIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32)
                | information.FileIndexLow),
            isDirectory,
            information.NumberOfLinks,
            information.FileAttributes,
            ((long)information.FileSizeHigh << 32)
            | information.FileSizeLow);
    }

    internal static ByHandleFileInformation GetRawInformation(
        SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(
                handle,
                out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return information;
    }

    internal static PromotionTreeSnapshot CaptureTree(
        SafeFileHandle root,
        IPromotionFileSystemTrustPolicy trustPolicy)
    {
        var rootInformation = GetVerifiedInformation(
            root,
            requireDirectory: true,
            requireSingleLinkFile: false);
        EnsureTrustedSecurity(
            trustPolicy,
            root,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
        var state = new CaptureState();
        CaptureDirectory(
            root,
            relativePrefix: string.Empty,
            depth: 0,
            state,
            trustPolicy);
        return new PromotionTreeSnapshot(
            rootInformation.Identity,
            state.Files
                .OrderBy(
                    entry => entry.RelativePath,
                    StringComparer.Ordinal)
                .ToArray(),
            state.Entries
                .OrderBy(
                    entry => entry.RelativePath,
                    StringComparer.Ordinal)
                .ToArray());
    }

    internal static async Task CopyTreeAsync(
        SafeFileHandle sourceRoot,
        SafeFileHandle destinationRoot,
        IPromotionFileSystemTrustPolicy trustPolicy,
        byte[] privateDirectorySecurity,
        byte[] privateFileSecurity,
        string ownedArtifactLogicalName,
        CancellationToken cancellationToken)
    {
        GetVerifiedInformation(
            sourceRoot,
            requireDirectory: true,
            requireSingleLinkFile: false);
        GetVerifiedInformation(
            destinationRoot,
            requireDirectory: true,
            requireSingleLinkFile: false);
        EnsureTrustedSecurity(
            trustPolicy,
            sourceRoot,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
        EnsureTrustedSecurity(
            trustPolicy,
            destinationRoot,
            requireCreateTrust: true,
            rejectUnsafeInheritance: true);
        var state = new CopyState();
        await CopyDirectoryAsync(
            sourceRoot,
            destinationRoot,
            depth: 0,
            state,
            trustPolicy,
            privateDirectorySecurity,
            privateFileSecurity,
            ownedArtifactLogicalName,
            cancellationToken);
    }

    internal static IReadOnlyList<string> ReadDirectoryNames(
        SafeFileHandle directory)
    {
        const int bufferLength = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            var names = new List<string>();
            var restartScan = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(
                    directory,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out _,
                    buffer,
                    bufferLength,
                    FileNamesInformation,
                    returnSingleEntry: false,
                    IntPtr.Zero,
                    restartScan);
                restartScan = false;
                if (status == StatusNoMoreFiles)
                {
                    break;
                }

                ThrowIfNtFailed(status);
                var offset = 0;
                while (true)
                {
                    var next = Marshal.ReadInt32(buffer, offset);
                    var nameLength =
                        Marshal.ReadInt32(buffer, offset + 8);
                    var name = Marshal.PtrToStringUni(
                        IntPtr.Add(buffer, offset + 12),
                        nameLength / sizeof(char))
                        ?? throw new PromotionFileSystemSafetyException(
                            "promotion-directory-entry-invalid");
                    if (name is not "." and not "..")
                    {
                        names.Add(name);
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    offset += next;
                }
            }

            return names
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void RenameRelative(
        SafeFileHandle source,
        SafeFileHandle destinationParent,
        string destinationName)
    {
        var bytes =
            System.Text.Encoding.Unicode.GetBytes(destinationName);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var nameLengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = nameLengthOffset + sizeof(uint);
        var bufferSize =
            nameOffset + bytes.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0;
                 index < bufferSize;
                 index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            Marshal.WriteIntPtr(
                buffer,
                rootOffset,
                destinationParent.DangerousGetHandle());
            Marshal.WriteInt32(
                buffer,
                nameLengthOffset,
                bytes.Length);
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
            var status = NtSetInformationFile(
                source,
                out _,
                buffer,
                bufferSize,
                FileRenameInformation);
            ThrowIfNtFailed(status);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void DeleteTreeContents(SafeFileHandle directory)
    {
        DeleteTreeContents(directory, depth: 0);
    }

    private static void DeleteTreeContents(
        SafeFileHandle directory,
        int depth)
    {
        if (depth > MaximumPromotionTreeDepth)
        {
            throw new InvalidDataException(
                "promotion-tree-depth-exceeded");
        }

        var names = ReadDirectoryNames(directory);
        foreach (var name in names)
        {
            using var child = OpenRelative(
                directory,
                name,
                Delete
                | FileReadData
                | FileReadAttributes
                | Synchronize,
                FileOpen,
                FileOpenReparsePoint,
                AllShareAccess);
            var information = GetVerifiedInformation(
                child,
                requireDirectory: null,
                requireSingleLinkFile: true);
            if (information.IsDirectory)
            {
                DeleteTreeContents(child, depth + 1);
            }

            DeleteByHandle(child);
        }

        EnsureDirectoryBecameEmpty(directory);
    }

    internal static void DeleteOwnedTreeContents(
        SafeFileHandle directory)
    {
        DeleteOwnedTreeContents(directory, depth: 0);
    }

    private static void DeleteOwnedTreeContents(
        SafeFileHandle directory,
        int depth)
    {
        if (depth > MaximumPromotionTreeDepth)
        {
            throw new InvalidDataException(
                "promotion-tree-depth-exceeded");
        }

        var names = ReadDirectoryNames(directory);
        foreach (var name in names)
        {
            using var child = OpenRelativeForDelete(
                directory,
                name);
            var information = GetRawInformation(child);
            var isReparse =
                (information.FileAttributes
                 & FileAttributeReparsePoint) != 0;
            var isDirectory =
                (information.FileAttributes
                 & FileAttributeDirectory) != 0;
            if (isDirectory && !isReparse)
            {
                DeleteOwnedTreeContents(child, depth + 1);
            }

            DeleteByHandle(child);
        }

        EnsureDirectoryBecameEmpty(directory);
    }

    internal static void DeleteByHandle(SafeFileHandle handle)
    {
        var flags = FileDispositionFlagDelete
                    | FileDispositionFlagPosixSemantics
                    | FileDispositionFlagIgnoreReadonlyAttribute;
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)flags));
            if (SetFileInformationByHandle(
                    handle,
                    FileDispositionInfoEx,
                    buffer,
                    sizeof(uint)))
            {
                return;
            }

            var nativeBuffer = Marshal.AllocHGlobal(sizeof(byte));
            try
            {
                Marshal.WriteByte(nativeBuffer, 0, 1);
                var nativeStatus = NtSetInformationFile(
                    handle,
                    out _,
                    nativeBuffer,
                    sizeof(byte),
                    FileDispositionInformation);
                if (nativeStatus >= 0)
                {
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }

            Marshal.WriteByte(buffer, 0, 1);
            if (!SetFileInformationByHandle(
                    handle,
                    FileDispositionInfo,
                    buffer,
                    sizeof(byte)))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool SnapshotsEqual(
        PromotionTreeSnapshot left,
        PromotionTreeSnapshot right) =>
        left.RootIdentity == right.RootIdentity
        && left.Files.SequenceEqual(right.Files)
        && left.Entries.SequenceEqual(right.Entries);

    private static SafeFileHandle? TryOpenRelative(
        SafeFileHandle root,
        string name,
        uint desiredAccess,
        uint createDisposition,
        uint createOptions,
        uint shareAccess,
        uint fileAttributes,
        bool throwIfMissing,
        bool allowReparse = false,
        byte[]? securityDescriptor = null,
        bool verifyAfterOpen = true)
    {
        if (name.Length == 0
            || name.Contains(
                Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || name.Contains(
                Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal)
            || name is "." or "..")
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-relative-name-invalid");
        }

        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringPointer =
            Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var securityDescriptorPointer = IntPtr.Zero;
        try
        {
            if (securityDescriptor is not null)
            {
                securityDescriptorPointer =
                    Marshal.AllocHGlobal(
                        securityDescriptor.Length);
                Marshal.Copy(
                    securityDescriptor,
                    0,
                    securityDescriptorPointer,
                    securityDescriptor.Length);
            }

            var byteLength = checked((ushort)(
                name.Length * sizeof(char)));
            Marshal.StructureToPtr(
                new UnicodeString(
                    byteLength,
                    checked((ushort)(
                        byteLength + sizeof(char))),
                    nameBuffer),
                unicodeStringPointer,
                fDeleteOld: false);
            var attributes = new ObjectAttributes(
                Marshal.SizeOf<ObjectAttributes>(),
                root.DangerousGetHandle(),
                unicodeStringPointer,
                ObjCaseInsensitive
                | (allowReparse ? 0 : ObjDontReparse),
                securityDescriptorPointer,
                IntPtr.Zero);
            var status = NtCreateFile(
                out var handle,
                desiredAccess | ReadControl,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes,
                shareAccess,
                createDisposition,
                createOptions | FileSynchronousIoNonAlert,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                handle?.Dispose();
                if (!throwIfMissing
                    && error is ErrorFileNotFound
                        or ErrorPathNotFound)
                {
                    return null;
                }

                if (!allowReparse
                    && error is ErrorCantAccessFile
                        or ErrorNotAReparsePoint)
                {
                    throw new PromotionFileSystemSafetyException(
                        "promotion-reparse-point");
                }

                throw new Win32Exception(error);
            }

            if (handle is null || handle.IsInvalid)
            {
                handle?.Dispose();
                throw new PromotionFileSystemSafetyException(
                    "promotion-relative-open-invalid");
            }

            if (!verifyAfterOpen)
            {
                return handle;
            }

            if (allowReparse)
            {
                GetRawInformation(handle);
            }
            else
            {
                GetVerifiedInformation(
                    handle,
                    requireDirectory: null,
                    requireSingleLinkFile: false);
            }

            return handle;
        }

        finally
        {
            if (securityDescriptorPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(
                    securityDescriptorPointer);
            }

            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static SafeFileHandle CreateRelativeOwned(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint createDisposition,
        uint createOptions,
        uint shareAccess,
        uint fileAttributes,
        byte[] securityDescriptor,
        bool requireDirectory,
        bool requireSingleLinkFile,
        Action<string>? afterCreateBeforeValidation,
        Action<string>? beforeConstructionCleanup,
        string residueReason,
        string residueLogicalName)
    {
        var handle = TryOpenRelative(
            parent,
            name,
            desiredAccess,
            createDisposition,
            createOptions,
            shareAccess,
            fileAttributes,
            throwIfMissing: true,
            securityDescriptor: securityDescriptor,
            verifyAfterOpen: false)
            ?? throw new Win32Exception(ErrorPathNotFound);
        try
        {
            afterCreateBeforeValidation?.Invoke(name);
            GetVerifiedInformation(
                handle,
                requireDirectory,
                requireSingleLinkFile);
            return handle;
        }
        catch (Exception operationException)
        {
            try
            {
                beforeConstructionCleanup?.Invoke(name);
                DeleteByHandle(handle);
                handle.Dispose();
                EnsureRelativeAbsent(parent, name);
                GetVerifiedInformation(
                    parent,
                    requireDirectory: true,
                    requireSingleLinkFile: false);
            }
            catch (Exception cleanupException)
            {
                handle.Dispose();
                throw new PromotionArtifactConstructionException(
                    residueReason,
                    residueLogicalName,
                    operationException,
                    cleanupException);
            }

            throw;
        }
    }

    private static void EnsureRelativeAbsent(
        SafeFileHandle parent,
        string name)
    {
        using var remaining = TryOpenRelative(
            parent,
            name,
            FileReadAttributes | Synchronize,
            FileOpen,
            FileOpenReparsePoint,
            AllShareAccess,
            fileAttributes: 0,
            throwIfMissing: false,
            allowReparse: true);
        if (remaining is not null)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-created-artifact-cleanup-unproven");
        }
    }

    private static SafeFileHandle OpenRelativeForDelete(
        SafeFileHandle root,
        string name)
    {
        var handle = TryOpenRelative(
            root,
            name,
            Delete
            | FileReadData
            | FileReadAttributes
            | Synchronize,
            FileOpen,
            FileOpenReparsePoint,
            AllShareAccess,
            fileAttributes: 0,
            throwIfMissing: true,
            allowReparse: true);
        return handle
            ?? throw new Win32Exception(ErrorPathNotFound);
    }

    private static void CaptureDirectory(
        SafeFileHandle directory,
        string relativePrefix,
        int depth,
        CaptureState state,
        IPromotionFileSystemTrustPolicy trustPolicy)
    {
        if (depth > MaximumPromotionTreeDepth)
        {
            throw new InvalidDataException(
                "promotion-tree-depth-exceeded");
        }

        state.DirectoryCount++;
        if (state.DirectoryCount
            > BaselineEvidenceValidator.MaximumEvidenceFileCount)
        {
            throw new InvalidDataException(
                "promotion-directory-count-exceeded");
        }

        var namesBefore = ReadDirectoryNames(directory);
        foreach (var name in namesBefore)
        {
            using var child = OpenRelative(
                directory,
                name,
                FileReadData
                | FileReadAttributes
                | Synchronize,
                FileOpen,
                FileOpenReparsePoint,
                AllShareAccess);
            var before = GetVerifiedInformation(
                child,
                requireDirectory: null,
                requireSingleLinkFile: true);
            EnsureTrustedSecurity(
                trustPolicy,
                child,
                requireCreateTrust: true,
                rejectUnsafeInheritance: before.IsDirectory);
            var relativePath = string.IsNullOrEmpty(relativePrefix)
                ? name
                : $"{relativePrefix}/{name}";
            if (before.IsDirectory)
            {
                state.Entries.Add(
                    new PromotionTreeIdentityEntry(
                        relativePath,
                        IsDirectory: true,
                        before.Identity,
                        before.LinkCount));
                CaptureDirectory(
                    child,
                    relativePath,
                    depth + 1,
                    state,
                    trustPolicy);
            }
            else
            {
                if (state.Files.Count
                        >= BaselineEvidenceValidator
                            .MaximumEvidenceFileCount
                    || before.Length
                        > BaselineEvidenceValidator
                            .MaximumEvidenceFileBytes
                    || before.Length
                        > BaselineEvidenceValidator
                            .MaximumEvidenceTreeBytes
                        - state.TotalBytes)
                {
                    throw new InvalidDataException(
                        "promotion-tree-limit-exceeded");
                }

                var hash = HashHandle(child, before.Length);
                var after = GetVerifiedInformation(
                    child,
                    requireDirectory: false,
                    requireSingleLinkFile: true);
                EnsureInformationStable(before, after);
                state.Files.Add(new BaselineFileManifestEntry(
                    relativePath,
                    before.Length,
                    hash));
                state.Entries.Add(
                    new PromotionTreeIdentityEntry(
                        relativePath,
                        IsDirectory: false,
                        before.Identity,
                        before.LinkCount));
                state.TotalBytes += before.Length;
            }

            var final = GetVerifiedInformation(
                child,
                requireDirectory: before.IsDirectory,
                requireSingleLinkFile: true);
            EnsureInformationStable(before, final);
        }

        var namesAfter = ReadDirectoryNames(directory);
        if (!namesBefore.SequenceEqual(namesAfter))
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-tree-identity-changed");
        }
    }

    private static async Task CopyDirectoryAsync(
        SafeFileHandle sourceDirectory,
        SafeFileHandle destinationDirectory,
        int depth,
        CopyState state,
        IPromotionFileSystemTrustPolicy trustPolicy,
        byte[] privateDirectorySecurity,
        byte[] privateFileSecurity,
        string ownedArtifactLogicalName,
        CancellationToken cancellationToken)
    {
        if (depth > MaximumPromotionTreeDepth)
        {
            throw new InvalidDataException(
                "promotion-tree-depth-exceeded");
        }

        state.DirectoryCount++;
        if (state.DirectoryCount
            > BaselineEvidenceValidator.MaximumEvidenceFileCount)
        {
            throw new InvalidDataException(
                "promotion-directory-count-exceeded");
        }

        var namesBefore = ReadDirectoryNames(sourceDirectory);
        foreach (var name in namesBefore)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = OpenRelative(
                sourceDirectory,
                name,
                FileReadData
                | FileReadAttributes
                | Synchronize,
                FileOpen,
                FileOpenReparsePoint,
                AllShareAccess);
            var before = GetVerifiedInformation(
                source,
                requireDirectory: null,
                requireSingleLinkFile: true);
            EnsureTrustedSecurity(
                trustPolicy,
                source,
                requireCreateTrust: true,
                rejectUnsafeInheritance: before.IsDirectory);
            if (before.IsDirectory)
            {
                using var destination =
                    CreateRelativeDirectory(
                        destinationDirectory,
                        name,
                        privateDirectorySecurity,
                        residueReason:
                        "promotion-candidate-construction-residue",
                        residueLogicalName:
                        ownedArtifactLogicalName);
                EnsureTrustedSecurity(
                    trustPolicy,
                    destination,
                    requireCreateTrust: true,
                    rejectUnsafeInheritance: true);
                await CopyDirectoryAsync(
                    source,
                    destination,
                    depth + 1,
                    state,
                    trustPolicy,
                    privateDirectorySecurity,
                    privateFileSecurity,
                    ownedArtifactLogicalName,
                    cancellationToken);
            }
            else
            {
                if (state.FileCount
                        >= BaselineEvidenceValidator
                            .MaximumEvidenceFileCount
                    || before.Length
                        > BaselineEvidenceValidator
                            .MaximumEvidenceFileBytes
                    || before.Length
                        > BaselineEvidenceValidator
                            .MaximumEvidenceTreeBytes
                        - state.TotalBytes)
                {
                    throw new InvalidDataException(
                        "promotion-tree-limit-exceeded");
                }

                using var destination = CreateRelativeFile(
                    destinationDirectory,
                    name,
                    privateFileSecurity,
                    residueReason:
                    "promotion-candidate-construction-residue",
                    residueLogicalName:
                    ownedArtifactLogicalName);
                EnsureTrustedSecurity(
                    trustPolicy,
                    destination,
                    requireCreateTrust: true,
                    rejectUnsafeInheritance: false);
                await CopyFileAsync(
                    source,
                    destination,
                    before.Length,
                    cancellationToken);
                var destinationInformation = GetVerifiedInformation(
                    destination,
                    requireDirectory: false,
                    requireSingleLinkFile: true);
                if (destinationInformation.Length != before.Length)
                {
                    throw new PromotionFileSystemSafetyException(
                        "promotion-copy-verification-failed");
                }

                state.FileCount++;
                state.TotalBytes += before.Length;
            }

            var after = GetVerifiedInformation(
                source,
                requireDirectory: before.IsDirectory,
                requireSingleLinkFile: true);
            EnsureInformationStable(before, after);
        }

        if (!namesBefore.SequenceEqual(
                ReadDirectoryNames(sourceDirectory)))
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-source-mutated");
        }
    }

    private static Task CopyFileAsync(
        SafeFileHandle source,
        SafeFileHandle destination,
        long length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(
                source,
                buffer,
                offset);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "promotion-source-short-read");
            }

            RandomAccess.Write(
                destination,
                buffer.AsSpan(0, read),
                offset);
            offset += read;
        }

        RandomAccess.FlushToDisk(destination);
        return Task.CompletedTask;
    }

    private static string HashHandle(
        SafeFileHandle handle,
        long expectedLength)
    {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long offset = 0;
        while (offset < expectedLength)
        {
            var read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0)
            {
                throw new EndOfStreamException(
                    "promotion-source-short-read");
            }

            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void EnsureInformationStable(
        FileInformation before,
        FileInformation after)
    {
        if (before.Identity != after.Identity
            || before.IsDirectory != after.IsDirectory
            || before.LinkCount != after.LinkCount
            || before.Length != after.Length
            || before.Attributes != after.Attributes)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-tree-identity-changed");
        }
    }

    private static void EnsureDirectoryBecameEmpty(
        SafeFileHandle directory)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (ReadDirectoryNames(directory).Count == 0)
            {
                return;
            }

            if (attempt < 5)
            {
                Thread.Sleep(attempt * 50);
            }
        }

        throw new PromotionFileSystemSafetyException(
            "promotion-cleanup-tree-changed");
    }

    private static void ThrowIfNtFailed(int status)
    {
        if (status < 0)
        {
            throw new Win32Exception(
                unchecked((int)RtlNtStatusToDosError(status)));
        }
    }

    internal readonly record struct FileInformation(
        PromotionArtifactIdentity Identity,
        bool IsDirectory,
        uint LinkCount,
        uint Attributes,
        long Length);

    private sealed class CaptureState
    {
        public List<BaselineFileManifestEntry> Files { get; } = [];

        public List<PromotionTreeIdentityEntry> Entries { get; } = [];

        public long TotalBytes { get; set; }

        public int DirectoryCount { get; set; }
    }

    private sealed class CopyState
    {
        public long TotalBytes { get; set; }

        public int FileCount { get; set; }

        public int DirectoryCount { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct UnicodeString(
        ushort Length,
        ushort MaximumLength,
        IntPtr Buffer);

    [StructLayout(LayoutKind.Sequential)]
    private record struct ObjectAttributes(
        int Length,
        IntPtr RootDirectory,
        IntPtr ObjectName,
        uint Attributes,
        IntPtr SecurityDescriptor,
        IntPtr SecurityQualityOfService);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IoStatusBlock(
        IntPtr Status,
        IntPtr Information);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(
        SafeFileHandle fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
