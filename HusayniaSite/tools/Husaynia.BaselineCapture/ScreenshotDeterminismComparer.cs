using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace Husaynia.BaselineCapture;

public sealed record VisualDiffBounds(int Left, int Top, int Right, int Bottom);

public sealed record ScreenshotVisualDifference(
    string Key,
    string RunASha256,
    string RunBSha256,
    string UnmaskedDiffPath,
    long DifferingPixelCount,
    double DifferingPixelRatio,
    VisualDiffBounds? Bounds);

public sealed record ScreenshotDeterminismEvidence(
    DateTimeOffset ComparedAtUtc,
    string RunACaptureId,
    string RunBCaptureId,
    DateTimeOffset RunACapturedAtUtc,
    DateTimeOffset RunBCapturedAtUtc,
    int RunAScreenshotCount,
    int RunBScreenshotCount,
    int MetricDifferenceCount,
    int PixelHashDifferenceCount,
    IReadOnlyList<string> ComparedMetrics,
    IReadOnlyList<string> MetricDifferences,
    IReadOnlyList<ScreenshotVisualDifference> PixelDifferences,
    string ReviewStatus,
    bool Passed)
{
    public IReadOnlyList<ScreenshotHashEvidence> ScreenshotHashes { get; init; } = [];
    public DateTimeOffset RunAStartedAtUtc { get; init; }
    public DateTimeOffset RunACompletedAtUtc { get; init; }
    public DateTimeOffset RunBStartedAtUtc { get; init; }
    public DateTimeOffset RunBCompletedAtUtc { get; init; }
}

public sealed record ScreenshotHashEvidence(
    string Key,
    string RunASha256,
    string RunBSha256);

public sealed record VisualReviewRectangle(int X, int Y, int Width, int Height, string Rationale);

public sealed record ScreenshotVisualReviewEntry(
    string Key,
    string RunASha256,
    string RunBSha256,
    string UnmaskedDiffPath,
    string? MaskVersion,
    IReadOnlyList<VisualReviewRectangle>? Rectangles,
    string Reviewer,
    [property: JsonRequired]
    DateTimeOffset ReviewedUtc,
    string Decision,
    string Rationale);

internal static class ScreenshotVisualReviewParser
{
    internal const int MaximumTextLength = 2048;
    private static readonly HashSet<string> EntryProperties =
        new(
            [
                "key", "runASha256", "runBSha256", "unmaskedDiffPath",
                "maskVersion", "rectangles", "reviewer", "reviewedUtc",
                "decision", "rationale"
            ],
            StringComparer.Ordinal);
    private static readonly HashSet<string> RectangleProperties =
        new(["x", "y", "width", "height", "rationale"], StringComparer.Ordinal);

    public static async Task<ScreenshotVisualReviewEntry[]> ParseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("visual-review-root-must-be-array");
        }

        return document.RootElement
            .EnumerateArray()
            .Select(ParseEntry)
            .ToArray();
    }

    private static ScreenshotVisualReviewEntry ParseEntry(JsonElement element)
    {
        RequireExactProperties(element, EntryProperties);
        var maskVersion = ReadNullableString(element, "maskVersion");
        VisualReviewRectangle[]? rectangles = null;
        var rectangleElement = element.GetProperty("rectangles");
        if (rectangleElement.ValueKind != JsonValueKind.Null)
        {
            if (rectangleElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("visual-review-rectangles-invalid");
            }

            rectangles = rectangleElement.EnumerateArray()
                .Select(ParseRectangle)
                .ToArray();
        }

        if (maskVersion is null != (rectangles is null)
            || maskVersion is not null
                && (maskVersion != "1"
                    || rectangles is null
                    || rectangles.Length is < 1 or > 64))
        {
            throw new JsonException("visual-review-mask-pair-invalid");
        }

        var decision = ReadBoundedNonBlankString(element, "decision");
        if (decision is not ("accept" or "reject"))
        {
            throw new JsonException("visual-review-decision-invalid");
        }

        DateTimeOffset reviewedUtc;
        try
        {
            reviewedUtc = element.GetProperty("reviewedUtc").GetDateTimeOffset();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            throw new JsonException("visual-review-timestamp-invalid", ex);
        }

        return new ScreenshotVisualReviewEntry(
            ReadBoundedNonBlankString(element, "key"),
            ReadBoundedNonBlankString(element, "runASha256"),
            ReadBoundedNonBlankString(element, "runBSha256"),
            ReadBoundedNonBlankString(element, "unmaskedDiffPath"),
            maskVersion,
            rectangles,
            ReadBoundedNonBlankString(element, "reviewer"),
            reviewedUtc,
            decision,
            ReadBoundedNonBlankString(element, "rationale"));
    }

    private static VisualReviewRectangle ParseRectangle(JsonElement element)
    {
        RequireExactProperties(element, RectangleProperties);
        var x = ReadInteger(element, "x");
        var y = ReadInteger(element, "y");
        var width = ReadInteger(element, "width");
        var height = ReadInteger(element, "height");
        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            throw new JsonException("visual-review-rectangle-nonpositive");
        }

        return new VisualReviewRectangle(
            x,
            y,
            width,
            height,
            ReadBoundedNonBlankString(element, "rationale"));
    }

    private static void RequireExactProperties(
        JsonElement element,
        HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("visual-review-object-required");
        }

        var names = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        if (names.Length != expected.Count
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || !names.ToHashSet(StringComparer.Ordinal).SetEquals(expected))
        {
            throw new JsonException("visual-review-properties-invalid");
        }
    }

    private static string ReadBoundedNonBlankString(
        JsonElement element,
        string propertyName)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"visual-review-{propertyName}-invalid");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
        {
            throw new JsonException($"visual-review-{propertyName}-invalid");
        }

        return text;
    }

    private static string? ReadNullableString(
        JsonElement element,
        string propertyName)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadBoundedNonBlankString(element, propertyName);
    }

    private static int ReadInteger(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result))
        {
            throw new JsonException($"visual-review-{propertyName}-invalid");
        }

        return result;
    }

    public static bool IsMaskInBounds(
        ScreenshotVisualReviewEntry entry,
        int imageWidth,
        int imageHeight) =>
        imageWidth > 0
        && imageHeight > 0
        && (entry.Rectangles is null
            || entry.Rectangles.All(rectangle =>
                rectangle.X >= 0
                && rectangle.Y >= 0
                && rectangle.Width > 0
                && rectangle.Height > 0
                && (long)rectangle.X + rectangle.Width <= imageWidth
                && (long)rectangle.Y + rectangle.Height <= imageHeight));
}

public static class ScreenshotDeterminismComparer
{
    internal const long MaximumComparisonPngBytesPerRun = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] ComparedMetrics =
    [
        "actualViewportWidth", "actualViewportHeight", "actualScreenWidth", "actualScreenHeight",
        "actualDevicePixelRatio", "documentScrollWidth", "documentScrollHeight", "bodyWidth",
        "bodyHeight", "textLength", "formCount", "horizontalOverflow", "fontsReady",
        "incompleteImageCount", "inFlightAllowedRequests", "visibleLandmarks", "overflowSources",
        "qualityStatus", "qualityReasonCodes"
    ];

    public static async Task<int> CompareAsync(
        string runAEvidenceDirectory,
        string runBEvidenceDirectory,
        string outputPath,
        string? visualReviewPath,
        CancellationToken cancellationToken)
    {
        var runA = Path.GetFullPath(runAEvidenceDirectory);
        var runB = Path.GetFullPath(runBEvidenceDirectory);
        CaptureIO.EnsureNoReparsePath(runA);
        CaptureIO.EnsureNoReparsePath(runB);
        CaptureIO.EnsureNoReparsePath(outputPath);
        if (visualReviewPath is not null)
        {
            CaptureIO.EnsureNoReparsePath(visualReviewPath);
        }

        var first = await ReadScreenshotsAsync(runA, cancellationToken);
        var second = await ReadScreenshotsAsync(runB, cancellationToken);
        var firstSummary = await ReadSummaryAsync(runA, cancellationToken);
        var secondSummary = await ReadSummaryAsync(runB, cancellationToken);
        var firstProvenance = await ReadProvenanceAsync(runA, cancellationToken);
        var secondProvenance = await ReadProvenanceAsync(runB, cancellationToken);
        var differences = ValidateNonVisualInputs(first, second, firstProvenance, secondProvenance);
        differences.AddRange(BaselineEvidenceValidator.ValidateControlledRuns(
            firstSummary,
            firstProvenance,
            secondSummary,
            secondProvenance));
        var runBValidation = await new BaselineEvidenceValidator()
            .ValidateScreenshotComparisonRunAsync(runB, cancellationToken);
        differences.AddRange(runBValidation.ReasonCodes.Select(
            reason => $"run-b:{reason}"));
        var firstPngs = await ValidatePngArtifactsAsync(
            "run-a",
            runA,
            first,
            differences,
            cancellationToken);
        var secondPngs = await ValidatePngArtifactsAsync(
            "run-b",
            runB,
            second,
            differences,
            cancellationToken);
        var firstByKey = first
            .GroupBy(Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var pixelDifferences = new List<ScreenshotVisualDifference>();
        var outputFullPath = Path.GetFullPath(outputPath);
        var canonicalOutputPath = Path.Combine(runA, "screenshot-determinism.json");
        if (!outputFullPath.Equals(canonicalOutputPath, StringComparison.OrdinalIgnoreCase)
            || visualReviewPath is not null
                && !Path.GetFullPath(visualReviewPath).Equals(
                    Path.Combine(runA, "screenshot-visual-review.json"),
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureSafetyException("comparison-output-not-canonical");
        }

        var outputDirectory = Path.GetDirectoryName(outputFullPath)
            ?? throw new ArgumentException("Comparison output directory could not be resolved.", nameof(outputPath));

        if (differences.Count == 0)
        {
            var changedKeys = firstByKey.Keys
                .Where(key => !string.Equals(
                    firstPngs[key].Inspection.Sha256,
                    secondPngs[key].Inspection.Sha256,
                    StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (changedKeys.Length > 0)
            {
                var diffDirectory = Path.Combine(outputDirectory, "screenshot-diffs");
                EnsureSafeDiffDirectory(runA, diffDirectory);
                if (Directory.Exists(diffDirectory))
                {
                    Directory.Delete(diffDirectory, recursive: true);
                }

                Directory.CreateDirectory(diffDirectory);
                await using var browser = await VerifiedBrowserSession.CreateAsync(
                    CaptureProfile.Approved,
                    ["MAP * ~NOTFOUND"],
                    cancellationToken);
                await using var context = await browser.Browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ServiceWorkers = ServiceWorkerPolicy.Block,
                    AcceptDownloads = false,
                    IgnoreHTTPSErrors = false
                }).WaitAsync(cancellationToken);
                var page = await context.NewPageAsync().WaitAsync(cancellationToken);
                foreach (var key in changedKeys)
                {
                    var firstBytes = await File.ReadAllBytesAsync(
                        firstPngs[key].Path,
                        cancellationToken);
                    var secondBytes = await File.ReadAllBytesAsync(
                        secondPngs[key].Path,
                        cancellationToken);
                    var diff = await ComparePixelsAsync(
                        page,
                        firstBytes,
                        secondBytes,
                        cancellationToken);
                    var relative = $"screenshot-diffs/{CaptureIO.SafeFileName(key)}.png";
                    await File.WriteAllBytesAsync(
                        Path.Combine(outputDirectory, relative.Replace('/', Path.DirectorySeparatorChar)),
                        diff.Png,
                        cancellationToken);
                    pixelDifferences.Add(new ScreenshotVisualDifference(
                        key,
                        firstPngs[key].Inspection.Sha256,
                        secondPngs[key].Inspection.Sha256,
                        relative,
                        diff.DifferingPixelCount,
                        diff.DifferingPixelRatio,
                        diff.Bounds));
                }
            }
        }

        var comparedAtUtc = DateTimeOffset.UtcNow;
        if (comparedAtUtc < secondSummary.CompletedAtUtc)
        {
            differences.Add("comparison-before-run-b-completed");
        }

        var reviewStatus = "not-required";
        var passed = differences.Count == 0 && pixelDifferences.Count == 0;
        var exitCode = passed ? 0 : 1;
        if (differences.Count == 0 && pixelDifferences.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(visualReviewPath) || !File.Exists(visualReviewPath))
            {
                reviewStatus = "required";
                exitCode = 3;
            }
            else
            {
                var reviewReasons = await ValidateReviewAsync(
                    visualReviewPath,
                    pixelDifferences,
                    firstByKey,
                    secondSummary.CompletedAtUtc,
                    comparedAtUtc,
                    cancellationToken);
                if (reviewReasons.Count == 0)
                {
                    reviewStatus = "accepted";
                    passed = true;
                    exitCode = 0;
                }
                else
                {
                    reviewStatus = "invalid";
                    differences.AddRange(reviewReasons);
                    exitCode = 1;
                }
            }
        }

        var screenshotHashes = firstPngs.Keys
            .Intersect(secondPngs.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(key => new ScreenshotHashEvidence(
                key,
                firstPngs[key].Inspection.Sha256,
                secondPngs[key].Inspection.Sha256))
            .ToArray();
        var evidence = new ScreenshotDeterminismEvidence(
            comparedAtUtc,
            firstProvenance.CaptureId,
            secondProvenance.CaptureId,
            firstProvenance.CapturedAtUtc,
            secondProvenance.CapturedAtUtc,
            first.Length,
            second.Length,
            differences.Count,
            pixelDifferences.Count,
            ComparedMetrics,
            differences.Order(StringComparer.Ordinal).ToArray(),
            pixelDifferences,
            reviewStatus,
            passed)
        {
            ScreenshotHashes = screenshotHashes,
            RunAStartedAtUtc = firstSummary.StartedAtUtc,
            RunACompletedAtUtc = firstSummary.CompletedAtUtc,
            RunBStartedAtUtc = secondSummary.StartedAtUtc,
            RunBCompletedAtUtc = secondSummary.CompletedAtUtc
        };
        await CaptureIO.WriteJsonAtomicAsync(outputFullPath, evidence, cancellationToken);
        return exitCode;
    }

    private static void EnsureSafeDiffDirectory(string runA, string diffDirectory)
    {
        var expected = Path.Combine(Path.GetFullPath(runA), "screenshot-diffs");
        if (!Path.GetFullPath(diffDirectory).Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureSafetyException("comparison-diff-directory-not-canonical");
        }

        CaptureIO.EnsureNoReparsePath(diffDirectory);
        if (Directory.Exists(diffDirectory)
            && (new DirectoryInfo(diffDirectory).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new CaptureSafetyException("comparison-diff-directory-reparse-point");
        }
    }

    private static List<string> ValidateNonVisualInputs(
        IReadOnlyList<ScreenshotRecord> first,
        IReadOnlyList<ScreenshotRecord> second,
        ScreenshotCaptureProvenance firstProvenance,
        ScreenshotCaptureProvenance secondProvenance)
    {
        var reasons = new List<string>();
        var expected = CaptureProfile.Approved.ExpectedScreenshotKeys;
        ValidateKeys("run-a", first, expected, reasons);
        ValidateKeys("run-b", second, expected, reasons);
        ValidateRepresentativeTuples("run-a", first, reasons);
        ValidateRepresentativeTuples("run-b", second, reasons);
        if (BaselineEvidenceValidator
            .ValidateBrowserIsolationProvenance(firstProvenance).Count > 0
            || BaselineEvidenceValidator
                .ValidateBrowserIsolationProvenance(secondProvenance).Count > 0)
        {
            reasons.Add("browser-isolation-provenance-mismatch");
        }

        if (!CompatibleProvenance(firstProvenance, secondProvenance))
        {
            reasons.Add("browser-provenance-mismatch");
        }

        if (reasons.Count > 0)
        {
            return reasons;
        }

        var secondByKey = second.ToDictionary(Key, StringComparer.Ordinal);
        foreach (var left in first.OrderBy(Key, StringComparer.Ordinal))
        {
            var key = Key(left);
            var right = secondByKey[key];
            if (left.Status != "captured"
                || right.Status != "captured"
                || left.QualityStatus != "pass"
                || right.QualityStatus != "pass")
            {
                reasons.Add($"{key}:capture-or-quality-failed");
                continue;
            }

            if (!MetricsEqual(left, right))
            {
                reasons.Add($"{key}:metrics-differ");
            }
        }

        return reasons;
    }

    private static void ValidateRepresentativeTuples(
        string runName,
        IReadOnlyList<ScreenshotRecord> screenshots,
        List<string> reasons)
    {
        foreach (var screenshot in screenshots)
        {
            var viewport = CaptureProfile.Approved.Viewports.SingleOrDefault(
                item => item.Name == screenshot.Viewport);
            if (!CaptureProfile.Approved.Representatives.TryGetValue(
                    screenshot.TemplateKey,
                    out var representative)
                || viewport is null
                || screenshot.Url != representative.AbsoluteUri
                || screenshot.Width != viewport.Width
                || screenshot.Height != viewport.Height
                || screenshot.NetworkDecisionRef
                    != "screenshot-network-decisions.json"
                || screenshot.ProvenanceRef
                    != "screenshot-capture-provenance.json")
            {
                reasons.Add(
                    $"{runName}:{Key(screenshot)}:representative-tuple-mismatch");
            }
        }
    }

    private static async Task<IReadOnlyDictionary<string, ValidatedPng>> ValidatePngArtifactsAsync(
        string runName,
        string root,
        IReadOnlyList<ScreenshotRecord> screenshots,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        var validated = new Dictionary<string, ValidatedPng>(StringComparer.Ordinal);
        var referencedPaths = new HashSet<string>(StringComparer.Ordinal);
        var screenshotDirectory = Path.Combine(root, "screenshots");
        var actualPaths = Directory.Exists(screenshotDirectory)
            ? EnumeratePngsWithoutReparse(root, screenshotDirectory, runName, reasons)
            : [];
        var aggregateWithinLimit = EnsureAggregatePngBytesWithinLimit(
            actualPaths.Select(relative => new FileInfo(Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar))).Length),
            out _);
        if (!aggregateWithinLimit)
        {
            reasons.Add($"{runName}:screenshot-png-aggregate-too-large");
        }

        foreach (var screenshot in screenshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(screenshot);
            if (string.IsNullOrWhiteSpace(screenshot.SavedAs))
            {
                reasons.Add($"{runName}:{key}:screenshot-path-missing");
                continue;
            }

            var relative = screenshot.SavedAs.Replace('\\', '/');
            if (!relative.StartsWith("screenshots/", StringComparison.Ordinal)
                || !relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || !referencedPaths.Add(relative))
            {
                reasons.Add($"{runName}:{key}:screenshot-path-invalid-or-duplicate");
                continue;
            }

            string path;
            try
            {
                path = ResolveScreenshotPath(root, screenshot);
            }
            catch (InvalidDataException)
            {
                reasons.Add($"{runName}:{key}:screenshot-path-escape");
                continue;
            }

            if (!File.Exists(path))
            {
                reasons.Add($"{runName}:{key}:screenshot-file-missing");
                continue;
            }

            if (PathContainsReparsePoint(root, path))
            {
                reasons.Add($"{runName}:{key}:screenshot-reparse-point");
                continue;
            }

            if (new FileInfo(path).Length > PngArtifactInspector.MaximumPngBytes)
            {
                reasons.Add($"{runName}:{key}:screenshot-png-too-large");
                continue;
            }

            if (!aggregateWithinLimit)
            {
                continue;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var inspection = PngArtifactInspector.Inspect(bytes);
                if (!string.Equals(inspection.Sha256, screenshot.Sha256, StringComparison.Ordinal))
                {
                    reasons.Add($"{runName}:{key}:screenshot-hash-mismatch");
                }

                if (inspection.Width != screenshot.Width
                    || inspection.Height != screenshot.Height
                    || inspection.Width != screenshot.PngWidth
                    || inspection.Height != screenshot.PngHeight)
                {
                    reasons.Add($"{runName}:{key}:screenshot-dimension-mismatch");
                }

                if (inspection.Length != screenshot.PngBytes
                    || Math.Abs(inspection.ByteEntropy - screenshot.ByteEntropy) > 0.000_001)
                {
                    reasons.Add($"{runName}:{key}:screenshot-metric-mismatch");
                }

                if (!validated.TryAdd(key, new ValidatedPng(path, inspection)))
                {
                    reasons.Add($"{runName}:duplicate-key:{key}");
                }
            }
            catch (InvalidDataException)
            {
                reasons.Add($"{runName}:{key}:screenshot-png-invalid");
            }
        }

        foreach (var extra in actualPaths.Except(referencedPaths, StringComparer.Ordinal))
        {
            try
            {
                var extraPath = Path.Combine(
                    root,
                    extra.Replace('/', Path.DirectorySeparatorChar));
                if (new FileInfo(extraPath).Length > PngArtifactInspector.MaximumPngBytes)
                {
                    reasons.Add($"{runName}:extra-png-too-large:{extra}");
                    continue;
                }

                if (aggregateWithinLimit)
                {
                    _ = PngArtifactInspector.Inspect(
                        await File.ReadAllBytesAsync(extraPath, cancellationToken));
                }
            }
            catch (InvalidDataException)
            {
                reasons.Add($"{runName}:extra-png-invalid:{extra}");
            }

            reasons.Add($"{runName}:extra-png:{extra}");
        }

        foreach (var missing in referencedPaths.Except(actualPaths, StringComparer.Ordinal))
        {
            reasons.Add($"{runName}:missing-png:{missing}");
        }

        return validated;
    }

    internal static bool EnsureAggregatePngBytesWithinLimit(
        IEnumerable<long> lengths,
        out long total)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        total = 0;
        foreach (var length in lengths)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lengths));
            }

            if (length > MaximumComparisonPngBytesPerRun - total)
            {
                total = MaximumComparisonPngBytesPerRun + 1;
                return false;
            }

            total += length;
        }

        return true;
    }

    private static HashSet<string> EnumeratePngsWithoutReparse(
        string root,
        string screenshotDirectory,
        string runName,
        List<string> reasons)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var directoryCount = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(screenshotDirectory));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            directoryCount++;
            if (directoryCount > BaselineEvidenceValidator.MaximumEvidenceFileCount)
            {
                reasons.Add($"{runName}:screenshot-directory-count-exceeded");
                break;
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                reasons.Add($"{runName}:screenshot-reparse-point");
                continue;
            }

            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reasons.Add(
                        $"{runName}:screenshot-reparse-point:{Path.GetRelativePath(root, entry.FullName).Replace('\\', '/')}");
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file
                         && file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    if (paths.Count >= BaselineEvidenceValidator.MaximumEvidencePngCount)
                    {
                        reasons.Add($"{runName}:screenshot-file-count-exceeded");
                        return paths;
                    }

                    paths.Add(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'));
                }
            }
        }

        return paths;
    }

    private static bool PathContainsReparsePoint(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = Path.GetFullPath(root);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateKeys(
        string runName,
        IReadOnlyList<ScreenshotRecord> rows,
        IReadOnlySet<string> expected,
        List<string> reasons)
    {
        var groups = rows.GroupBy(Key, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in groups.Where(group => group.Count() != 1))
        {
            reasons.Add($"{runName}:duplicate-key:{duplicate.Key}");
        }

        var actual = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in expected.Except(actual, StringComparer.Ordinal))
        {
            reasons.Add($"{runName}:missing-key:{missing}");
        }

        foreach (var extra in actual.Except(expected, StringComparer.Ordinal))
        {
            reasons.Add($"{runName}:extra-key:{extra}");
        }
    }

    private static bool CompatibleProvenance(
        ScreenshotCaptureProvenance left,
        ScreenshotCaptureProvenance right)
    {
        var approvedHash = CaptureProfile.Approved
            .GetCurrentBrowserIdentity()
            .ExecutableSha256;
        return left.OperatingSystem == right.OperatingSystem
        && left.Architecture == right.Architecture
        && left.PlaywrightPackageVersion == CaptureProfile.PlaywrightVersion
        && right.PlaywrightPackageVersion == CaptureProfile.PlaywrightVersion
        && left.ChromiumRevision == CaptureProfile.ChromiumRevision
        && right.ChromiumRevision == CaptureProfile.ChromiumRevision
        && left.ChromiumVersion == CaptureProfile.ChromiumVersion
        && right.ChromiumVersion == CaptureProfile.ChromiumVersion
        && left.ExpectedExecutableSha256 == approvedHash
        && left.ActualExecutableSha256 == approvedHash
        && right.ExpectedExecutableSha256 == approvedHash
        && right.ActualExecutableSha256 == approvedHash
        && left.ChromiumSandbox
        && right.ChromiumSandbox
        && left.NetworkPolicyVersion == right.NetworkPolicyVersion
        && left.PrimaryOrigin == right.PrimaryOrigin;
    }

    private static bool MetricsEqual(ScreenshotRecord left, ScreenshotRecord right) =>
        left.Width == right.Width
        && left.Height == right.Height
        && left.PngWidth == right.PngWidth
        && left.PngHeight == right.PngHeight
        && left.ActualViewportWidth == right.ActualViewportWidth
        && left.ActualViewportHeight == right.ActualViewportHeight
        && left.ActualScreenWidth == right.ActualScreenWidth
        && left.ActualScreenHeight == right.ActualScreenHeight
        && left.ActualDevicePixelRatio.Equals(right.ActualDevicePixelRatio)
        && left.DocumentScrollWidth == right.DocumentScrollWidth
        && left.DocumentScrollHeight == right.DocumentScrollHeight
        && left.BodyWidth.Equals(right.BodyWidth)
        && left.BodyHeight.Equals(right.BodyHeight)
        && left.TextLength == right.TextLength
        && left.FormCount == right.FormCount
        && left.HorizontalOverflow == right.HorizontalOverflow
        && left.FontsReady == right.FontsReady
        && left.IncompleteImageCount == right.IncompleteImageCount
        && left.InFlightAllowedRequests == right.InFlightAllowedRequests
        && left.VisibleLandmarks.SequenceEqual(right.VisibleLandmarks)
        && left.OverflowSources.SequenceEqual(right.OverflowSources)
        && left.QualityReasonCodes.SequenceEqual(right.QualityReasonCodes);

    private static async Task<IReadOnlyList<string>> ValidateReviewAsync(
        string reviewPath,
        IReadOnlyList<ScreenshotVisualDifference> differences,
        Dictionary<string, ScreenshotRecord> screenshots,
        DateTimeOffset runBCompletedAtUtc,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        ScreenshotVisualReviewEntry[] entries;
        try
        {
            entries = await ScreenshotVisualReviewParser.ParseAsync(
                reviewPath,
                cancellationToken);
        }
        catch (JsonException)
        {
            return ["visual-review-json-invalid"];
        }

        var reasons = new List<string>();
        var groups = entries.GroupBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        foreach (var duplicate in groups.Where(group => group.Count() != 1))
        {
            reasons.Add($"visual-review-duplicate:{duplicate.Key}");
        }

        var byKey = groups.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var difference in differences)
        {
            if (!byKey.TryGetValue(difference.Key, out var review))
            {
                reasons.Add($"visual-review-missing:{difference.Key}");
                continue;
            }

            if (review.RunASha256 != difference.RunASha256
                || review.RunBSha256 != difference.RunBSha256
                || review.UnmaskedDiffPath != difference.UnmaskedDiffPath)
            {
                reasons.Add($"visual-review-stale:{difference.Key}");
            }

            if (review.Decision != "accept")
            {
                reasons.Add($"visual-review-not-accepted:{difference.Key}");
            }

            if (string.IsNullOrWhiteSpace(review.Reviewer)
                || string.IsNullOrWhiteSpace(review.Rationale)
                || !IsSha256(review.RunASha256)
                || !IsSha256(review.RunBSha256)
                || !IsValidReviewedUtc(
                    review.ReviewedUtc,
                    runBCompletedAtUtc,
                    utcNow))
            {
                reasons.Add($"visual-review-incomplete:{difference.Key}");
            }

            if (!screenshots.TryGetValue(difference.Key, out var screenshot)
                || !ScreenshotVisualReviewParser.IsMaskInBounds(
                    review,
                    screenshot.PngWidth,
                    screenshot.PngHeight))
            {
                reasons.Add($"visual-review-mask-bounds-invalid:{difference.Key}");
            }
        }

        foreach (var extra in byKey.Keys.Except(
                     differences.Select(item => item.Key),
                     StringComparer.Ordinal))
        {
            reasons.Add($"visual-review-extra:{extra}");
        }

        return reasons;
    }

    internal static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    internal static bool IsValidReviewedUtc(
        DateTimeOffset reviewedUtc,
        DateTimeOffset runBCompletedAtUtc,
        DateTimeOffset utcNow) =>
        reviewedUtc != default
        && reviewedUtc.Offset == TimeSpan.Zero
        && reviewedUtc >= runBCompletedAtUtc
        && reviewedUtc <= utcNow;

    private static async Task<PixelDiffResult> ComparePixelsAsync(
        IPage page,
        byte[] first,
        byte[] second,
        CancellationToken cancellationToken)
    {
        var result = await page.EvaluateAsync<JsonElement>(
            """
            async ({ first, second }) => {
              const load = value => new Promise((resolve, reject) => {
                const image = new Image();
                image.onload = () => resolve(image);
                image.onerror = reject;
                image.src = `data:image/png;base64,${value}`;
              });
              const [a, b] = await Promise.all([load(first), load(second)]);
              if (a.width !== b.width || a.height !== b.height) throw new Error('pixel-dimension-mismatch');
              const canvasA = document.createElement('canvas');
              const canvasB = document.createElement('canvas');
              const diff = document.createElement('canvas');
              canvasA.width = canvasB.width = diff.width = a.width;
              canvasA.height = canvasB.height = diff.height = a.height;
              const contextA = canvasA.getContext('2d', { willReadFrequently: true });
              const contextB = canvasB.getContext('2d', { willReadFrequently: true });
              const contextDiff = diff.getContext('2d');
              contextA.drawImage(a, 0, 0);
              contextB.drawImage(b, 0, 0);
              const pixelsA = contextA.getImageData(0, 0, a.width, a.height);
              const pixelsB = contextB.getImageData(0, 0, b.width, b.height);
              const output = contextDiff.createImageData(a.width, a.height);
              let count = 0;
              let left = a.width, top = a.height, right = -1, bottom = -1;
              for (let index = 0; index < pixelsA.data.length; index += 4) {
                const changed = pixelsA.data[index] !== pixelsB.data[index]
                  || pixelsA.data[index + 1] !== pixelsB.data[index + 1]
                  || pixelsA.data[index + 2] !== pixelsB.data[index + 2]
                  || pixelsA.data[index + 3] !== pixelsB.data[index + 3];
                if (!changed) continue;
                const pixel = index / 4;
                const x = pixel % a.width;
                const y = Math.floor(pixel / a.width);
                count++;
                left = Math.min(left, x); top = Math.min(top, y);
                right = Math.max(right, x); bottom = Math.max(bottom, y);
                output.data[index] = 255;
                output.data[index + 1] = Math.abs(pixelsA.data[index + 1] - pixelsB.data[index + 1]);
                output.data[index + 2] = 255;
                output.data[index + 3] = 255;
              }
              contextDiff.putImageData(output, 0, 0);
              return {
                count,
                ratio: count / (a.width * a.height),
                bounds: count === 0 ? null : { left, top, right, bottom },
                png: diff.toDataURL('image/png').split(',', 2)[1]
              };
            }
            """,
            new
            {
                first = Convert.ToBase64String(first),
                second = Convert.ToBase64String(second)
            }).WaitAsync(cancellationToken);
        var bounds = result.GetProperty("bounds").ValueKind == JsonValueKind.Null
            ? null
            : new VisualDiffBounds(
                result.GetProperty("bounds").GetProperty("left").GetInt32(),
                result.GetProperty("bounds").GetProperty("top").GetInt32(),
                result.GetProperty("bounds").GetProperty("right").GetInt32(),
                result.GetProperty("bounds").GetProperty("bottom").GetInt32());
        return new PixelDiffResult(
            result.GetProperty("count").GetInt64(),
            result.GetProperty("ratio").GetDouble(),
            bounds,
            Convert.FromBase64String(result.GetProperty("png").GetString()!));
    }

    private static async Task<ScreenshotRecord[]> ReadScreenshotsAsync(
        string directory,
        CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ScreenshotRecord[]>(
            await File.ReadAllTextAsync(Path.Combine(directory, "screenshots.json"), cancellationToken),
            CaseInsensitiveJson) ?? [];

    private static async Task<CaptureSummary> ReadSummaryAsync(
        string directory,
        CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<CaptureSummary>(
            await File.ReadAllTextAsync(
                Path.Combine(directory, "capture-summary.json"),
                cancellationToken),
            CaseInsensitiveJson)
        ?? throw new InvalidDataException("capture-summary.json is invalid.");

    private static async Task<ScreenshotCaptureProvenance> ReadProvenanceAsync(
        string directory,
        CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
            await File.ReadAllTextAsync(
                Path.Combine(directory, "screenshot-capture-provenance.json"),
                cancellationToken),
            CaseInsensitiveJson)
        ?? throw new InvalidDataException("screenshot-capture-provenance.json is invalid.");

    private static string ResolveScreenshotPath(string root, ScreenshotRecord screenshot)
    {
        if (string.IsNullOrWhiteSpace(screenshot.SavedAs))
        {
            throw new InvalidDataException($"{Key(screenshot)} has no screenshot path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            screenshot.SavedAs.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("screenshot-path-escape");
        }

        return fullPath;
    }

    private static string Key(ScreenshotRecord item) => $"{item.TemplateKey}|{item.Viewport}";

    private sealed record PixelDiffResult(
        long DifferingPixelCount,
        double DifferingPixelRatio,
        VisualDiffBounds? Bounds,
        byte[] Png);

    private sealed record ValidatedPng(
        string Path,
        PngArtifactInspection Inspection);
}
