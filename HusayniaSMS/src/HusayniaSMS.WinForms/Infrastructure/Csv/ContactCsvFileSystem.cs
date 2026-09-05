using System.ComponentModel;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HusayniaSMS.WinForms.Infrastructure.Csv;

internal readonly record struct ContactCsvFileMetadata(
    long Length,
    DateTimeOffset LastWriteTimeUtc);

internal readonly record struct ContactCsvCommitResult(string? SafeDiagnostic);

internal sealed class AtomicReplaceUnavailableException : IOException
{
    public AtomicReplaceUnavailableException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal sealed class SecureTemporaryFileUnavailableException : IOException
{
    public SecureTemporaryFileUnavailableException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal interface IContactCsvFileSystem
{
    string GetFullPath(string path);
    bool FileExists(string fullPath);
    ContactCsvFileMetadata GetMetadata(string fullPath);
    Stream OpenRead(string fullPath);
    Stream CreateSiblingTemporaryFile(
        string destinationFullPath,
        out string temporaryFullPath);
    Task FlushToDiskAsync(Stream stream, CancellationToken cancellationToken);
    void SetLastWriteTimeUtc(string fullPath, DateTimeOffset lastWriteTimeUtc);
    ContactCsvCommitResult CommitNew(
        string temporaryFullPath,
        string destinationFullPath);
    ContactCsvCommitResult ReplaceExisting(
        string temporaryFullPath,
        string destinationFullPath);
    void DeleteFile(string fullPath);
}

internal sealed class WindowsContactCsvFileSystem : IContactCsvFileSystem
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorNotSupported = 50;

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool FileExists(string fullPath) => File.Exists(fullPath);

    public ContactCsvFileMetadata GetMetadata(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new(info.Length, info.LastWriteTimeUtc);
    }

    public Stream OpenRead(string fullPath) => new FileStream(
        fullPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public Stream CreateSiblingTemporaryFile(
        string destinationFullPath,
        out string temporaryFullPath)
    {
        var directory = Path.GetDirectoryName(destinationFullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException("The CSV destination directory is unavailable.");
        }

        var destinationName = Path.GetFileName(destinationFullPath);
        temporaryFullPath = Path.Combine(
            directory,
            $".{destinationName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var security = File.Exists(destinationFullPath)
                ? FileSystemAclExtensions.GetAccessControl(
                    new FileInfo(destinationFullPath),
                    AccessControlSections.Access)
                : CreateOwnerOnlySecurity();
            return FileSystemAclExtensions.Create(
                new FileInfo(temporaryFullPath),
                FileMode.CreateNew,
                FileSystemRights.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough,
                security);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw SecureTemporaryFileUnavailable(exception);
        }
        catch (NotSupportedException exception)
        {
            throw SecureTemporaryFileUnavailable(exception);
        }
    }

    public async Task FlushToDiskAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (stream is FileStream fileStream)
        {
            fileStream.Flush(flushToDisk: true);
        }
        else
        {
            stream.Flush();
        }
    }

    public void SetLastWriteTimeUtc(
        string fullPath,
        DateTimeOffset lastWriteTimeUtc) =>
        File.SetLastWriteTimeUtc(fullPath, lastWriteTimeUtc.UtcDateTime);

    public ContactCsvCommitResult CommitNew(
        string temporaryFullPath,
        string destinationFullPath)
    {
        File.Move(temporaryFullPath, destinationFullPath, overwrite: false);
        return default;
    }

    public ContactCsvCommitResult ReplaceExisting(
        string temporaryFullPath,
        string destinationFullPath)
    {
        try
        {
            File.Replace(
                temporaryFullPath,
                destinationFullPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return default;
        }
        catch (PlatformNotSupportedException exception)
        {
            throw Unavailable(exception);
        }
        catch (NotSupportedException exception)
        {
            throw Unavailable(exception);
        }
        catch (IOException exception) when (IsAtomicReplacementUnavailable(exception))
        {
            throw Unavailable(exception);
        }
    }

    public void DeleteFile(string fullPath) => File.Delete(fullPath);

    private static FileSecurity CreateOwnerOnlySecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ??
            throw new UnauthorizedAccessException(
                "The current Windows user identity is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static bool IsAtomicReplacementUnavailable(IOException exception)
    {
        var nativeCode = new Win32Exception(exception.HResult & 0xFFFF).NativeErrorCode;
        return nativeCode is ErrorInvalidFunction or ErrorNotSameDevice or ErrorNotSupported;
    }

    private static AtomicReplaceUnavailableException Unavailable(Exception exception) =>
        new("Atomic CSV replacement is unavailable for this destination.", exception);

    private static SecureTemporaryFileUnavailableException SecureTemporaryFileUnavailable(
        Exception exception) =>
        new("Secure temporary CSV permissions are unavailable for this destination.", exception);
}
