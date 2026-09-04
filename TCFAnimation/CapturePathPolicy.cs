using System;
using System.Collections.Generic;
using System.IO;

namespace TCFAnimation;

public readonly record struct CaptureRootContext(
    bool IsStandalone,
    string ExecutablePath,
    string ApplicationBaseDirectory,
    string ProjectResourceRoot)
{
    public static CaptureRootContext FromGodotFeatures(
        bool hasStandaloneFeature,
        bool hasTemplateFeature,
        string executablePath,
        string applicationBaseDirectory,
        string projectResourceRoot)
    {
        return new CaptureRootContext(
            hasStandaloneFeature || hasTemplateFeature,
            executablePath,
            applicationBaseDirectory,
            projectResourceRoot);
    }
}

public readonly record struct CaptureRootResolution(
    string Root,
    string Kind);

public static class CaptureRootResolver
{
    public const string ExecutableAdjacentKind = "executable_adjacent";
    public const string ProjectResourceKind = "project_resource";

    public static CaptureRootResolution ResolveRoot(
        CaptureRootContext context,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        if (!context.IsStandalone)
        {
            return new CaptureRootResolution(
                ValidateDirectory(
                    context.ProjectResourceRoot,
                    nameof(context.ProjectResourceRoot),
                    directoryExists,
                    getAttributes),
                ProjectResourceKind);
        }

        string applicationRoot = ValidateDirectory(
            context.ApplicationBaseDirectory,
            nameof(context.ApplicationBaseDirectory),
            directoryExists,
            getAttributes);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            context.ExecutablePath,
            nameof(context.ExecutablePath));
        if (!Path.IsPathFullyQualified(context.ExecutablePath))
        {
            throw new ArgumentException(
                "Process executable must be a fully qualified filesystem path.",
                nameof(context.ExecutablePath));
        }
        string executablePath = Path.GetFullPath(context.ExecutablePath);
        fileExists ??= File.Exists;
        if (!fileExists(executablePath))
        {
            throw new FileNotFoundException(
                "Process executable does not exist.",
                executablePath);
        }
        getAttributes ??= File.GetAttributes;
        FileAttributes executableAttributes = getAttributes(executablePath);
        if (
            (executableAttributes & FileAttributes.Directory) != 0
            || (executableAttributes & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new IOException(
                $"Process executable is not a regular file: {executablePath}");
        }

        string executableDirectory = ValidateDirectory(
            Path.GetDirectoryName(executablePath)
                ?? throw new ArgumentException(
                    "Process executable has no filesystem directory.",
                    nameof(context.ExecutablePath)),
            nameof(context.ExecutablePath),
            directoryExists,
            getAttributes);
        if (
            !string.Equals(
                applicationRoot,
                executableDirectory,
                StringComparison.OrdinalIgnoreCase)
        )
        {
            string expectedManagedDirectory =
                $"data_{Path.GetFileNameWithoutExtension(executablePath)}"
                + "_windows_x86_64";
            string? applicationParent =
                Path.GetDirectoryName(applicationRoot);
            if (
                !string.Equals(
                    applicationParent,
                    executableDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(applicationRoot),
                    expectedManagedDirectory,
                    StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new IOException(
                    "Standalone application base is outside the approved "
                    + $"portable export layout: {applicationRoot}");
            }
        }

        return new CaptureRootResolution(
            executableDirectory,
            ExecutableAdjacentKind);
    }

    private static string ValidateDirectory(
        string candidate,
        string parameterName,
        Func<string, bool>? directoryExists,
        Func<string, FileAttributes>? getAttributes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate, parameterName);
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new ArgumentException(
                "Capture root must be a fully qualified filesystem path.",
                parameterName);
        }

        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(candidate));
        directoryExists ??= Directory.Exists;
        if (!directoryExists(root))
        {
            throw new DirectoryNotFoundException(
                $"Capture root does not exist: {root}");
        }

        getAttributes ??= File.GetAttributes;
        FileAttributes attributes = getAttributes(root);
        if (
            (attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new IOException(
                $"Capture root is not a regular directory: {root}");
        }

        return root;
    }
}

public static class CapturePathPolicy
{
    public const string CaptureDirectoryName = "Captures";
    public const string StagingFileExtension = ".staging";

    public static string Resolve(
        string projectRoot,
        string requestedFileName,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedFileName);

        if (
            Path.IsPathRooted(requestedFileName)
            || requestedFileName.Contains(Path.DirectorySeparatorChar)
            || requestedFileName.Contains(Path.AltDirectorySeparatorChar)
            || requestedFileName.Contains(Path.VolumeSeparatorChar)
            || requestedFileName.IndexOfAny(
                Path.GetInvalidFileNameChars()) >= 0
            || requestedFileName.EndsWith(' ')
            || requestedFileName.EndsWith('.')
            || requestedFileName is "." or ".."
            || IsWindowsDeviceName(requestedFileName)
        )
        {
            throw new ArgumentException(
                "Capture path must be a file name without directories.",
                nameof(requestedFileName));
        }

        if (
            !string.Equals(
                Path.GetExtension(requestedFileName),
                ".png",
                StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException(
                "Capture file name must use the .png extension.",
                nameof(requestedFileName));
        }

        string captureDirectory = GetCaptureDirectory(projectRoot);
        string capturePath = Path.GetFullPath(
            Path.Combine(captureDirectory, requestedFileName));
        if (!IsDirectChild(captureDirectory, capturePath))
        {
            throw new ArgumentException(
                "Capture path escapes the project capture directory.",
                nameof(requestedFileName));
        }

        fileExists ??= File.Exists;
        if (fileExists(capturePath))
        {
            throw new IOException(
                $"Capture file already exists: {capturePath}");
        }

        return capturePath;
    }

    public static string GetCaptureDirectory(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.GetFullPath(
            Path.Combine(Path.GetFullPath(projectRoot), CaptureDirectoryName));
    }

    public static string CreateStagingPath(
        string projectRoot,
        string capturePath,
        string? uniqueToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        string captureDirectory = GetCaptureDirectory(projectRoot);
        string finalPath = Path.GetFullPath(capturePath);
        if (!IsDirectChild(captureDirectory, finalPath))
        {
            throw new ArgumentException(
                "Final capture path must be inside the capture directory.",
                nameof(capturePath));
        }

        uniqueToken ??= Guid.NewGuid().ToString("N");
        if (!IsValidUniqueToken(uniqueToken))
        {
            throw new ArgumentException(
                "Staging token must be a 32-character hexadecimal value.",
                nameof(uniqueToken));
        }

        string stagingPath = Path.GetFullPath(
            Path.Combine(
                captureDirectory,
                $".{Path.GetFileName(finalPath)}.{uniqueToken}"
                + StagingFileExtension));
        if (!IsDirectChild(captureDirectory, stagingPath))
        {
            throw new InvalidOperationException(
                "Staging path escaped the capture directory.");
        }

        return stagingPath;
    }

    public static void EnsureNoReparsePoints(
        string projectRoot,
        Func<string, FileAttributes>? getAttributes = null)
    {
        getAttributes ??= File.GetAttributes;
        foreach (string component in EnumeratePathComponents(
            GetCaptureDirectory(projectRoot)))
        {
            FileAttributes attributes = getAttributes(component);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Capture path component is a reparse point: {component}");
            }
        }
    }

    public static void EnsureSafeToPublish(
        string projectRoot,
        string capturePath,
        string stagingPath,
        Func<string, FileAttributes>? getAttributes = null,
        Func<string, bool>? fileExists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        string captureDirectory = GetCaptureDirectory(projectRoot);
        string finalPath = Path.GetFullPath(capturePath);
        string stagedPath = Path.GetFullPath(stagingPath);
        if (
            !IsDirectChild(captureDirectory, finalPath)
            || !IsDirectChild(captureDirectory, stagedPath)
            || !IsStagingPathForCapture(finalPath, stagedPath)
        )
        {
            throw new ArgumentException(
                "Capture publication paths must stay inside Captures.");
        }

        getAttributes ??= File.GetAttributes;
        EnsureNoReparsePoints(projectRoot, getAttributes);

        FileAttributes stagingAttributes = getAttributes(stagedPath);
        if (
            (stagingAttributes & FileAttributes.ReparsePoint) != 0
            || (stagingAttributes & FileAttributes.Directory) != 0
        )
        {
            throw new IOException(
                $"Capture staging path is not a regular file: {stagedPath}");
        }

        fileExists ??= File.Exists;
        if (fileExists(finalPath))
        {
            throw new IOException(
                $"Capture file already exists: {finalPath}");
        }
    }

    private static bool IsWindowsDeviceName(string fileName)
    {
        string baseName = fileName.Split('.')[0].TrimEnd(' ', '.');
        if (
            baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return baseName.Length == 4
            && (
                baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
            )
            && (
                baseName[3] is >= '1' and <= '9'
                || baseName[3] is '¹' or '²' or '³'
            );
    }

    private static bool IsDirectChild(
        string expectedDirectory,
        string candidatePath)
    {
        return string.Equals(
            Path.GetDirectoryName(candidatePath),
            expectedDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidUniqueToken(string token)
    {
        if (token.Length != 32)
        {
            return false;
        }

        foreach (char character in token)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStagingPathForCapture(
        string capturePath,
        string stagingPath)
    {
        string stagingName = Path.GetFileName(stagingPath);
        string prefix = $".{Path.GetFileName(capturePath)}.";
        if (
            !stagingName.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase)
            || !stagingName.EndsWith(
                StagingFileExtension,
                StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        int tokenLength =
            stagingName.Length - prefix.Length - StagingFileExtension.Length;
        return tokenLength > 0
            && IsValidUniqueToken(
                stagingName.Substring(prefix.Length, tokenLength));
    }

    private static IEnumerable<string> EnumeratePathComponents(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException(
                "Capture path must have a filesystem root.",
                nameof(path));
        yield return root;

        string current = root;
        foreach (
            string component in fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
        )
        {
            current = Path.Combine(current, component);
            yield return current;
        }
    }
}
