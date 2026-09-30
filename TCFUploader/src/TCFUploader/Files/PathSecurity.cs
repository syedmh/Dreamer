using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TCFUploader.Files;

internal sealed class TrustedRoot : IDisposable
{
    private readonly SafeFileHandle? retainedHandle;
    private readonly RootIdentity identity;

    private TrustedRoot(
        string lexicalPath,
        string physicalPath,
        SafeFileHandle? retainedHandle,
        RootIdentity identity)
    {
        LexicalPath = lexicalPath;
        PhysicalPath = physicalPath;
        this.retainedHandle = retainedHandle;
        this.identity = identity;
    }

    internal string LexicalPath { get; }
    internal string PhysicalPath { get; }

    internal static bool TryCreate(string path, out TrustedRoot? trustedRoot, bool retainHandle = false)
    {
        trustedRoot = null;
        try
        {
            var lexical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(lexical) || !PathSecurity.HasNoReparsePointComponents(lexical))
                return false;

            if (!OperatingSystem.IsWindows())
            {
                trustedRoot = new TrustedRoot(
                    lexical, PathSecurity.GetPhysicalPathForComparison(lexical), null, default);
                return true;
            }

            SafeFileHandle? handle = PathSecurity.OpenDirectoryHandle(lexical);
            try
            {
                var physical = PathSecurity.GetFinalPath(handle);
                if (!string.Equals(lexical, physical, StringComparison.OrdinalIgnoreCase))
                    return false;
                trustedRoot = new TrustedRoot(
                    lexical, physical, retainHandle ? handle : null, PathSecurity.GetIdentity(handle));
                if (retainHandle)
                    handle = null;
                return true;
            }
            finally
            {
                handle?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal bool VerifyCurrent()
    {
        try
        {
            if (!Directory.Exists(LexicalPath) ||
                !PathSecurity.HasNoReparsePointComponents(LexicalPath))
                return false;
            if (!OperatingSystem.IsWindows())
                return string.Equals(
                    PhysicalPath,
                    PathSecurity.GetPhysicalPathForComparison(LexicalPath),
                    StringComparison.OrdinalIgnoreCase);

            using var current = PathSecurity.OpenDirectoryHandle(LexicalPath);
            return identity == PathSecurity.GetIdentity(current) &&
                string.Equals(PhysicalPath, PathSecurity.GetFinalPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal bool IsSafeCandidate(string path)
    {
        try
        {
            var candidate = Path.GetFullPath(path);
            if (!VerifyCurrent() || !PathSecurity.IsContainedOrEqual(LexicalPath, candidate))
                return false;
            if (string.Equals(
                LexicalPath,
                Path.TrimEndingDirectorySeparator(candidate),
                StringComparison.OrdinalIgnoreCase))
                return true;

            var relative = Path.GetRelativePath(LexicalPath, candidate);
            var current = LexicalPath;
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                    return false;
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal bool ContainsOpenFile(FileStream stream)
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            return VerifyCurrent() &&
                PathSecurity.IsContainedOrEqual(PhysicalPath, PathSecurity.GetFinalPath(stream.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() => retainedHandle?.Dispose();
}

internal static class PathSecurity
{
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static bool IsSafeWatchRoot(string path)
    {
        if (!TrustedRoot.TryCreate(path, out var trustedRoot))
            return false;
        trustedRoot!.Dispose();
        return true;
    }

    internal static bool IsSafeCandidate(string watchedRoot, string path)
    {
        if (!TrustedRoot.TryCreate(watchedRoot, out var trustedRoot))
            return false;
        using (trustedRoot)
            return trustedRoot!.IsSafeCandidate(path);
    }

    internal static bool IsOpenFileUnderRoot(string watchedRoot, FileStream stream)
    {
        if (!TrustedRoot.TryCreate(watchedRoot, out var trustedRoot))
            return false;
        using (trustedRoot)
            return trustedRoot!.ContainsOpenFile(stream);
    }

    internal static string GetPhysicalPathForComparison(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Path has no root.", nameof(path));
        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(current, segment);
            if (File.Exists(next) || Directory.Exists(next))
            {
                var info = (File.GetAttributes(next) & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(next)
                    : new FileInfo(next);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    next = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Unable to resolve reparse point.");
            }
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(next));
        }
        return current;
    }

    internal static bool IsContainedOrEqual(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasNoReparsePointComponents(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Path has no root.", nameof(path));
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            return false;
        foreach (var segment in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    internal static void HardenPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var current = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The current Windows identity has no SID.");
        var security = new DirectorySecurity();
        security.SetOwner(current);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            current,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    internal static bool HasPrivateDirectoryAcl(string path)
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            var current = WindowsIdentity.GetCurrent().User;
            if (current is null)
                return false;
            var security = new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                !owner.Equals(current) || !security.AreAccessRulesProtected)
                return false;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference is SecurityIdentifier sid &&
                    !sid.Equals(current))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
            IdentityNotMappedException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static SafeFileHandle OpenDirectoryHandle(string path)
    {
        var handle = CreateFile(
            path,
            0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("Unable to open the trusted root.");
        }
        return handle;
    }

    internal static RootIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Unable to read filesystem identity.");
        return new RootIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    internal static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
            throw new IOException("Unable to resolve open file path.");
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
                throw new IOException("Unable to resolve open file path.");
        }
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return Path.TrimEndingDirectorySeparator(@"\\" + path[8..]);
        return Path.TrimEndingDirectorySeparator(
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, char[] lpszFilePath, uint cchFilePath, uint dwFlags);
}

internal readonly record struct RootIdentity(uint VolumeSerialNumber, ulong FileIndex);
