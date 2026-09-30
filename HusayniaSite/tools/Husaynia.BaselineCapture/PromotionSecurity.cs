using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Husaynia.BaselineCapture;

public interface IPromotionFileSystemTrustPolicy
{
    void EnsureTrustedHandle(
        SafeFileHandle handle,
        bool requireCreateTrust,
        bool rejectUnsafeInheritance);
}

public sealed class PhysicalPromotionFileSystemTrustPolicy
    : IPromotionFileSystemTrustPolicy
{
    public static void ProtectArtifact(string path)
    {
        var fullPath = Path.GetFullPath(path);
        CaptureIO.EnsureNoReparsePath(fullPath);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            throw new InvalidDataException(
                "promotion-filesystem-artifact-missing");
        }

        ApplyPrivatePermissions(fullPath);
        VerifySinglePath(fullPath, requireCreateTrust: true);
    }

    public void EnsureTrustedHandle(
        SafeFileHandle handle,
        bool requireCreateTrust,
        bool rejectUnsafeInheritance)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "promotion-handle-security-unavailable");
        }

        VerifyWindowsSecurityDescriptor(
            ReadWindowsSecurityDescriptor(handle),
            requireCreateTrust,
            rejectUnsafeInheritance);
    }

    [SupportedOSPlatform("windows")]
    internal static byte[] CreatePrivateSecurityDescriptor(
        bool isDirectory)
    {
        var currentUser = CurrentWindowsUser();
        var inheritance = isDirectory
            ? AceFlags.ContainerInherit | AceFlags.ObjectInherit
            : AceFlags.None;
        var identities = new[]
        {
            currentUser,
            LocalSystem(),
            Administrators()
        };
        var access = new RawAcl(
            GenericAcl.AclRevision,
            identities.Length);
        foreach (var identity in identities)
        {
            access.InsertAce(
                access.Count,
                new CommonAce(
                    inheritance,
                    AceQualifier.AccessAllowed,
                    (int)FileSystemRights.FullControl,
                    identity,
                    isCallback: false,
                    opaque: null));
        }

        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent
            | ControlFlags.DiscretionaryAclProtected
            | ControlFlags.SelfRelative,
            currentUser,
            currentUser,
            systemAcl: null,
            discretionaryAcl: access);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void ApplyPrivatePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsPermissions(path);
            return;
        }

        var mode = Directory.Exists(path)
            ? UnixFileMode.UserRead
              | UnixFileMode.UserWrite
              | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, mode);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsPermissions(string path)
    {
        var currentUser = CurrentWindowsUser();
        if (Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);
            AddDirectoryRule(security, currentUser);
            AddDirectoryRule(security, LocalSystem());
            AddDirectoryRule(security, Administrators());
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        var fileSecurity = new FileSecurity();
        fileSecurity.SetOwner(currentUser);
        fileSecurity.SetAccessRuleProtection(
            isProtected: true,
            preserveInheritance: false);
        AddFileRule(fileSecurity, currentUser);
        AddFileRule(fileSecurity, LocalSystem());
        AddFileRule(fileSecurity, Administrators());
        new FileInfo(path).SetAccessControl(fileSecurity);
    }

    [SupportedOSPlatform("windows")]
    private static void AddDirectoryRule(
        DirectorySecurity security,
        SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    [SupportedOSPlatform("windows")]
    private static void AddFileRule(
        FileSecurity security,
        SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

    private static void VerifySinglePath(
        string path,
        bool requireCreateTrust)
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsPermissions(path, requireCreateTrust);
            return;
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0
            || ReadUnixOwner(path) != GetEffectiveUserId())
        {
            throw new InvalidDataException(
                "promotion-filesystem-permissions-untrusted");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsPermissions(
        string path,
        bool requireCreateTrust)
    {
        var currentUser = CurrentWindowsUser();
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier))
            as SecurityIdentifier
            ?? throw new InvalidDataException(
                "promotion-filesystem-owner-untrusted");
        if (!IsAllowedWindowsIdentity(owner, currentUser))
        {
            throw new InvalidDataException(
                "promotion-filesystem-owner-untrusted");
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            var identity =
                (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType != AccessControlType.Allow
                || IsAllowedWindowsIdentity(identity, currentUser))
            {
                continue;
            }

            var appliesToCurrent =
                (rule.PropagationFlags
                 & PropagationFlags.InheritOnly) == 0;
            var appliesToChildren =
                (rule.InheritanceFlags
                 & (InheritanceFlags.ContainerInherit
                    | InheritanceFlags.ObjectInherit)) != 0;
            if (appliesToCurrent
                    && HasUnsafeCapability(
                        rule.FileSystemRights,
                        requireCreateTrust)
                || requireCreateTrust
                    && appliesToChildren
                    && HasUnsafeCapability(
                        rule.FileSystemRights,
                        requireCreateTrust: true))
            {
                throw new InvalidDataException(
                    "promotion-filesystem-permissions-untrusted");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsSecurityDescriptor(
        RawSecurityDescriptor security,
        bool requireCreateTrust,
        bool rejectUnsafeInheritance)
    {
        var currentUser = CurrentWindowsUser();
        var owner = security.Owner
            ?? throw new InvalidDataException(
                "promotion-filesystem-owner-untrusted");
        if (!IsAllowedWindowsIdentity(owner, currentUser))
        {
            throw new InvalidDataException(
                "promotion-filesystem-owner-untrusted");
        }

        var access = security.DiscretionaryAcl
            ?? throw new InvalidDataException(
                "promotion-filesystem-permissions-untrusted");
        foreach (GenericAce genericAce in access)
        {
            if (genericAce is not QualifiedAce ace
                || ace.AceQualifier != AceQualifier.AccessAllowed
                || IsAllowedWindowsIdentity(
                    ace.SecurityIdentifier,
                    currentUser))
            {
                continue;
            }

            var appliesToCurrent =
                (ace.AceFlags & AceFlags.InheritOnly) == 0;
            var appliesToChildren =
                (ace.AceFlags
                 & (AceFlags.ContainerInherit
                    | AceFlags.ObjectInherit)) != 0;
            if (appliesToCurrent
                    && HasUnsafeCapability(
                        ace.AccessMask,
                        requireCreateTrust)
                || rejectUnsafeInheritance
                    && appliesToChildren
                    && HasUnsafeCapability(
                        ace.AccessMask,
                        requireCreateTrust: true))
            {
                throw new InvalidDataException(
                    "promotion-filesystem-permissions-untrusted");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAllowedWindowsIdentity(
        SecurityIdentifier identity,
        SecurityIdentifier currentUser) =>
        identity.Equals(currentUser)
        || identity.Equals(LocalSystem())
        || identity.Equals(Administrators())
        || identity.Equals(TrustedInstaller());

    [SupportedOSPlatform("windows")]
    private static bool HasUnsafeCapability(
        FileSystemRights rights,
        bool requireCreateTrust) =>
        HasUnsafeCapability((int)rights, requireCreateTrust);

    [SupportedOSPlatform("windows")]
    private static bool HasUnsafeCapability(
        int accessMask,
        bool requireCreateTrust)
    {
        const FileSystemRights renameCapabilities =
            FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;
        const FileSystemRights createCapabilities =
            FileSystemRights.WriteData
            | FileSystemRights.CreateFiles
            | FileSystemRights.AppendData
            | FileSystemRights.CreateDirectories
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes;
        var capabilities = renameCapabilities;
        if (requireCreateTrust)
        {
            capabilities |= createCapabilities;
        }

        const uint GenericAll = 0x10000000;
        const uint GenericWrite = 0x40000000;
        var rights = (FileSystemRights)accessMask;
        var genericRights = unchecked((uint)accessMask);
        return (rights & capabilities) != 0
            || (genericRights & GenericAll) != 0
            || requireCreateTrust
                && (genericRights & GenericWrite) != 0;
    }

    [SupportedOSPlatform("windows")]
    private static RawSecurityDescriptor ReadWindowsSecurityDescriptor(
        SafeFileHandle handle)
    {
        const int ErrorInsufficientBuffer = 122;
        const SecurityInfos requested =
            SecurityInfos.Owner | SecurityInfos.DiscretionaryAcl;
        _ = GetKernelObjectSecurity(
            handle,
            requested,
            securityDescriptor: null,
            length: 0,
            out var required);
        var error = Marshal.GetLastWin32Error();
        if (required == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        var buffer = new byte[required];
        if (!GetKernelObjectSecurity(
                handle,
                requested,
                buffer,
                checked((uint)buffer.Length),
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error());
        }

        return new RawSecurityDescriptor(buffer, 0);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentWindowsUser() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidDataException(
            "promotion-filesystem-current-user-missing");

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier LocalSystem() =>
        new(WellKnownSidType.LocalSystemSid, null);

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier Administrators() =>
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier TrustedInstaller() =>
        new(
            "S-1-5-80-956008885-3418522649-1831038044-"
            + "1853292631-2271478464");

    private static uint ReadUnixOwner(string path)
    {
        const string statPath = "/usr/bin/stat";
        if (!File.Exists(statPath))
        {
            throw new PlatformNotSupportedException(
                "promotion-filesystem-owner-check-unavailable");
        }

        var startInfo = new ProcessStartInfo(statPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsMacOS())
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("%u");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("%u");
        }

        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(path);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidDataException(
                "promotion-filesystem-owner-check-unavailable");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0
            || !uint.TryParse(
                output.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var owner))
        {
            throw new InvalidDataException(
                "promotion-filesystem-owner-check-unavailable");
        }

        return owner;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [SupportedOSPlatform("windows")]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle handle,
        SecurityInfos requestedInformation,
        byte[]? securityDescriptor,
        uint length,
        out uint lengthNeeded);

    private static uint GetEffectiveUserId()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return geteuid();
    }
}
