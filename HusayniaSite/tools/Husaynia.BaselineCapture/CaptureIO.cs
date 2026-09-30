using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;

namespace Husaynia.BaselineCapture;

public static class CaptureIO
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteJsonAsync(temporary, value, cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task WriteTextAtomicAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                value,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static async Task WriteChecksumsAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var checksumPath = Path.Combine(root, "checksums.sha256");
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Equals(checksumPath, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            var hash = await Sha256FileAsync(file, cancellationToken);
            lines.Add($"{hash}  {Path.GetRelativePath(root, file).Replace('\\', '/')}");
        }

        await WriteTextAtomicAsync(
            checksumPath,
            string.Join('\n', lines) + "\n",
            cancellationToken);
    }

    public static void EnsureEmptyOutputDirectory(string outputDirectory)
    {
        var fullPath = Path.GetFullPath(outputDirectory);
        EnsureNoReparsePath(fullPath);
        if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
        {
            throw new CaptureSafetyException("output-directory-not-empty");
        }

        Directory.CreateDirectory(fullPath);
    }

    public static void EnsureNoReparsePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException("path-root-missing");
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CaptureSafetyException("reparse-point-not-allowed");
            }
        }
    }

    public static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24
            || bytes[0] != 0x89
            || bytes[1] != 0x50
            || bytes[2] != 0x4e
            || bytes[3] != 0x47)
        {
            return (0, 0);
        }

        return (
            BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
    }

    public static double ComputeByteEntropy(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        Span<int> frequencies = stackalloc int[256];
        foreach (var value in bytes)
        {
            frequencies[value]++;
        }

        var entropy = 0d;
        foreach (var frequency in frequencies)
        {
            if (frequency == 0)
            {
                continue;
            }

            var probability = (double)frequency / bytes.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    public static string StableKey(string prefix, string value)
    {
        var hash = Sha256(Encoding.UTF8.GetBytes(value))[..16];
        return $"{prefix}-{hash}";
    }

    public static string SafeFileName(string value)
    {
        var decoded = Uri.UnescapeDataString(value).Normalize(NormalizationForm.FormC).Trim('/');
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return "home";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(decoded.Select(c => invalid.Contains(c) || c is '/' or '\\' or '?' or '#' ? '_' : c).ToArray());
        return cleaned.Length <= 100 ? cleaned : $"{cleaned[..80]}-{StableKey("p", value)[2..]}";
    }
}
