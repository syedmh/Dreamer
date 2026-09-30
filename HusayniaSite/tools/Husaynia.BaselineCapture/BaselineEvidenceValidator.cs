using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Husaynia.BaselineCapture;

public sealed record BaselineFileManifestEntry(
    string RelativePath,
    long Length,
    string Sha256);

public sealed record BaselineValidationResult(
    bool Passed,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<BaselineFileManifestEntry> Files);

public sealed class BaselineEvidenceValidator
{
    private const string EvidenceSchemaVersion = "1.0.0";
    internal const int MaximumEvidenceFileCount = 16_384;
    internal const long MaximumEvidenceFileBytes = 64L * 1024 * 1024;
    internal const long MaximumEvidenceTreeBytes = 1024L * 1024 * 1024;
    internal const int MaximumEvidencePngCount = 4096;
    internal const long MaximumEvidencePngBytes = 512L * 1024 * 1024;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly string[] SensitiveFailureFragments =
    [
        "token",
        "secret",
        "password",
        "cookie",
        "authorization",
        "credential"
    ];
    private static readonly HashSet<string> ExpectedChildEnvironmentKeys =
        new(
            OperatingSystem.IsWindows()
                ? ["SystemRoot", "TEMP", "TMP", "WINDIR"]
                : ["HOME", "TMPDIR"],
            StringComparer.OrdinalIgnoreCase);
    internal static IReadOnlySet<string> ApprovedChildEnvironmentKeys =>
        ExpectedChildEnvironmentKeys;

    private readonly CaptureProfile _profile = CaptureProfile.Approved;

    private static readonly string[] RequiredBaseArtifacts =
    [
        "capture-summary.json",
        "route-manifest.json",
        "migration-import-manifest.json",
        "http-inventory.json",
        "metadata-inventory.json",
        "media-inventory.json",
        "navigation-inventory.json",
        "forms-widgets.json",
        "religious-content.json",
        "screenshots.json",
        "screenshot-network-policy.json",
        "screenshot-network-decisions.json",
        "screenshot-capture-provenance.json",
        "screenshot-determinism.json",
        "residual-risks.json",
        "README.md"
    ];

    private static readonly string[] RequiredScreenshotComparisonArtifacts =
    [
        "capture-summary.json",
        "screenshots.json",
        "screenshot-network-policy.json",
        "screenshot-network-decisions.json",
        "screenshot-capture-provenance.json",
        "checksums.sha256"
    ];

    private const string GateStart = "<!-- ADR009-GATE-START -->";
    private const string GateEnd = "<!-- ADR009-GATE-END -->";

    public Task<BaselineValidationResult> ValidateReadOnlyAsync(
        string evidenceDirectory,
        CancellationToken cancellationToken)
    {
        EnsureApprovedProfile();
        return ValidateCoreAsync(
            evidenceDirectory,
            requireSealed: true,
            cancellationToken);
    }

    public async Task<BaselineValidationResult> FinalizeAsync(
        string evidenceDirectory,
        CancellationToken cancellationToken)
    {
        EnsureApprovedProfile();
        var alreadySealed = await ValidateCoreAsync(
            evidenceDirectory,
            requireSealed: true,
            cancellationToken);
        if (alreadySealed.Passed)
        {
            return alreadySealed;
        }

        var preseal = await ValidateCoreAsync(
            evidenceDirectory,
            requireSealed: false,
            cancellationToken);
        if (!preseal.Passed)
        {
            return preseal;
        }

        var root = Path.GetFullPath(evidenceDirectory);
        var summary = await ReadJsonAsync<CaptureSummary>(
            Path.Combine(root, "capture-summary.json"),
            cancellationToken);
        var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
            Path.Combine(root, "screenshots.json"),
            cancellationToken);
        var determinism = await ReadJsonAsync<ScreenshotDeterminismEvidence>(
            Path.Combine(root, "screenshot-determinism.json"),
            cancellationToken);
        var readmePath = Path.Combine(root, "README.md");
        var currentReadme = await File.ReadAllTextAsync(readmePath, cancellationToken);
        var desiredReadme = UpdateGateSection(currentReadme, summary, screenshots, determinism);
        var inputFiles = preseal.Files
            .Where(file => file.RelativePath is not ("checksums.sha256" or "baseline-verification.json"))
            .Select(file => file.RelativePath == "README.md"
                ? new BaselineFileManifestEntry(
                    file.RelativePath,
                    Utf8NoBom.GetByteCount(desiredReadme),
                    CaptureIO.Sha256(Utf8NoBom.GetBytes(desiredReadme)))
                : file)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var verification = new BaselineVerificationDocument(
            "1.0.0",
            summary.CaptureId,
            summary.CompletedAtUtc,
            true,
            screenshots.Length,
            screenshots.Count(item => item.Status == "captured"),
            screenshots.Count(item => item.QualityStatus == "pass"),
            determinism.RunBCaptureId,
            determinism.PixelHashDifferenceCount,
            determinism.ReviewStatus,
            inputFiles);
        await CaptureIO.WriteJsonAtomicAsync(
            Path.Combine(root, "baseline-verification.json"),
            verification,
            cancellationToken);
        await WriteTextAtomicAsync(readmePath, desiredReadme, cancellationToken);
        await CaptureIO.WriteChecksumsAsync(root, cancellationToken);
        return await ValidateCoreAsync(root, requireSealed: true, cancellationToken);
    }

    internal async Task<BaselineValidationResult> ValidateScreenshotComparisonRunAsync(
        string evidenceDirectory,
        CancellationToken cancellationToken)
    {
        EnsureApprovedProfile();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(evidenceDirectory);
        if (!Directory.Exists(root))
        {
            return new(false, ["evidence-directory-missing"], []);
        }

        var paths = EnumeratePathsWithoutTraversal(root, reasons);
        if (reasons.Any(reason =>
                reason.StartsWith("evidence-tree-", StringComparison.Ordinal)
                || reason.StartsWith("reparse-point", StringComparison.Ordinal)))
        {
            return Result(reasons, []);
        }

        var files = new List<BaselineFileManifestEntry>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(root, path);
            if (!IsAllowedScreenshotComparisonPath(relative))
            {
                reasons.Add($"comparison-unexpected-file:{relative}");
            }

            var length = new FileInfo(path).Length;
            if (length > MaximumEvidenceFileBytes)
            {
                reasons.Add($"evidence-file-too-large:{relative}");
                continue;
            }

            files.Add(new BaselineFileManifestEntry(
                relative,
                length,
                await CaptureIO.Sha256FileAsync(path, cancellationToken)));
        }

        var relativeFiles = files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var required in RequiredScreenshotComparisonArtifacts)
        {
            if (!relativeFiles.Contains(required))
            {
                reasons.Add($"required-artifact-missing:{required}");
            }
            else if (files.Single(file => file.RelativePath == required).Length == 0)
            {
                reasons.Add($"required-artifact-empty:{required}");
            }
        }

        if (reasons.Any(reason =>
                reason.StartsWith("required-artifact-", StringComparison.Ordinal)))
        {
            return Result(reasons, files);
        }

        try
        {
            await ValidateComparisonChecksumManifestAsync(
                root,
                files,
                reasons,
                cancellationToken);
            var summary = await ReadJsonAsync<CaptureSummary>(
                Path.Combine(root, "capture-summary.json"),
                cancellationToken);
            var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
                Path.Combine(root, "screenshots.json"),
                cancellationToken);
            var provenance = await ReadJsonAsync<ScreenshotCaptureProvenance>(
                Path.Combine(root, "screenshot-capture-provenance.json"),
                cancellationToken);
            var networkPolicy = await ReadJsonAsync<ScreenshotNetworkPolicyDocument>(
                Path.Combine(root, "screenshot-network-policy.json"),
                cancellationToken);
            ValidateScreenshotComparisonLineage(
                summary,
                provenance,
                reasons);
            ValidateProvenance(provenance, screenshots, reasons);
            ValidateNetworkPolicy(networkPolicy, provenance, reasons);
            var ledgerSnapshots = await ValidateDecisionLogAsync(
                root,
                screenshots,
                provenance,
                reasons,
                cancellationToken);
            await ValidateScreenshotsAsync(
                root,
                files,
                summary,
                screenshots,
                ledgerSnapshots,
                reasons,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reasons.Add($"evidence-parse-failed:{ex.GetType().Name}");
        }

        return Result(reasons, files);
    }

    private void EnsureApprovedProfile()
    {
        if (!ReferenceEquals(_profile, CaptureProfile.Approved))
        {
            throw new InvalidOperationException("capture-profile-not-approved");
        }
    }

    private static async Task<BaselineValidationResult> ValidateCoreAsync(
        string evidenceDirectory,
        bool requireSealed,
        CancellationToken cancellationToken)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(evidenceDirectory);
        if (!Directory.Exists(root))
        {
            return new(false, ["evidence-directory-missing"], []);
        }

        var paths = EnumeratePathsWithoutTraversal(root, reasons);
        if (reasons.Any(reason =>
                reason.StartsWith("evidence-tree-", StringComparison.Ordinal)
                || reason.StartsWith("reparse-point", StringComparison.Ordinal)))
        {
            return Result(reasons, []);
        }

        var files = new List<BaselineFileManifestEntry>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(root, path);
            if (!IsAllowedEvidencePath(relative))
            {
                reasons.Add($"unexpected-evidence-file:{relative}");
            }

            var length = new FileInfo(path).Length;
            if (length > MaximumEvidenceFileBytes)
            {
                reasons.Add($"evidence-file-too-large:{relative}");
                continue;
            }

            files.Add(new BaselineFileManifestEntry(
                relative,
                length,
                await CaptureIO.Sha256FileAsync(path, cancellationToken)));
        }

        var relativeFiles = files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var required in RequiredBaseArtifacts)
        {
            if (!relativeFiles.Contains(required))
            {
                reasons.Add($"required-artifact-missing:{required}");
            }
            else if (files.Single(file => file.RelativePath == required).Length == 0)
            {
                reasons.Add($"required-artifact-empty:{required}");
            }
        }

        if (reasons.Any(reason => reason.StartsWith("required-artifact-", StringComparison.Ordinal)))
        {
            return Result(reasons, files);
        }

        try
        {
            await ValidateRequiredArtifactShapesAsync(root, reasons, cancellationToken);
            var summary = await ReadJsonAsync<CaptureSummary>(
                Path.Combine(root, "capture-summary.json"),
                cancellationToken);
            var routeManifest = await ReadJsonAsync<RouteManifest>(
                Path.Combine(root, "route-manifest.json"),
                cancellationToken);
            var importManifest = await ReadJsonAsync<ImportManifest>(
                Path.Combine(root, "migration-import-manifest.json"),
                cancellationToken);
            var httpRecords = await ReadJsonAsync<HttpRecord[]>(
                Path.Combine(root, "http-inventory.json"),
                cancellationToken);
            var metadata = await ReadJsonAsync<MetadataRecord[]>(
                Path.Combine(root, "metadata-inventory.json"),
                cancellationToken);
            var assets = await ReadJsonAsync<AssetRecord[]>(
                Path.Combine(root, "media-inventory.json"),
                cancellationToken);
            var navigation = await ReadJsonAsync<NavigationItem[]>(
                Path.Combine(root, "navigation-inventory.json"),
                cancellationToken);
            var forms = await ReadJsonAsync<FormsWidgetsDocument>(
                Path.Combine(root, "forms-widgets.json"),
                cancellationToken);
            var religiousContent = await ReadJsonAsync<ReligiousContentRecord[]>(
                Path.Combine(root, "religious-content.json"),
                cancellationToken);
            var screenshots = await ReadJsonAsync<ScreenshotRecord[]>(
                Path.Combine(root, "screenshots.json"),
                cancellationToken);
            var provenance = await ReadJsonAsync<ScreenshotCaptureProvenance>(
                Path.Combine(root, "screenshot-capture-provenance.json"),
                cancellationToken);
            var networkPolicy = await ReadJsonAsync<ScreenshotNetworkPolicyDocument>(
                Path.Combine(root, "screenshot-network-policy.json"),
                cancellationToken);
            var determinism = await ReadJsonAsync<ScreenshotDeterminismEvidence>(
                Path.Combine(root, "screenshot-determinism.json"),
                cancellationToken);
            var risks = await ReadJsonAsync<ResidualRisk[]>(
                Path.Combine(root, "residual-risks.json"),
                cancellationToken);
            ValidateLineage(root, summary, screenshots, provenance, determinism, reasons);
            ValidateProvenance(provenance, screenshots, reasons);
            ValidateNetworkPolicy(networkPolicy, provenance, reasons);
            var ledgerSnapshots = await ValidateDecisionLogAsync(
                root,
                screenshots,
                provenance,
                reasons,
                cancellationToken);
            await ValidateScreenshotsAsync(
                root,
                files,
                summary,
                screenshots,
                ledgerSnapshots,
                reasons,
                cancellationToken);
            await ValidateManifestsAsync(
                root,
                files,
                summary,
                routeManifest,
                importManifest,
                httpRecords,
                metadata,
                assets,
                navigation,
                forms,
                religiousContent,
                risks,
                reasons,
                cancellationToken);
            await ValidateDeterminismAndReviewAsync(
                root,
                screenshots,
                determinism,
                reasons,
                cancellationToken);
            await ValidateAllPngsAsync(root, files, reasons, cancellationToken);
            if (requireSealed)
            {
                await ValidateSealAsync(root, files, summary, screenshots, determinism, reasons, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reasons.Add($"evidence-parse-failed:{ex.GetType().Name}");
        }

        return Result(reasons, files);
    }

    private static void ValidateLineage(
        string root,
        CaptureSummary summary,
        ScreenshotRecord[] screenshots,
        ScreenshotCaptureProvenance provenance,
        ScreenshotDeterminismEvidence determinism,
        HashSet<string> reasons)
    {
        foreach (var reason in CaptureRunLineageReasons(summary, provenance))
        {
            reasons.Add(reason);
        }

        if (summary.CaptureId != determinism.RunACaptureId
            || determinism.RunAStartedAtUtc != summary.StartedAtUtc
            || determinism.RunACapturedAtUtc != provenance.CapturedAtUtc
            || determinism.RunACompletedAtUtc != summary.CompletedAtUtc)
        {
            reasons.Add("capture-lineage-mismatch");
        }

        ValidateControlledRunValues(
            determinism.RunACaptureId,
            determinism.RunAStartedAtUtc,
            determinism.RunACapturedAtUtc,
            determinism.RunACompletedAtUtc,
            determinism.RunBCaptureId,
            determinism.RunBStartedAtUtc,
            determinism.RunBCapturedAtUtc,
            determinism.RunBCompletedAtUtc,
            reasons);
        if (determinism.ComparedAtUtc < determinism.RunBCompletedAtUtc)
        {
            reasons.Add("determinism-lineage-mismatch");
        }

        if (screenshots.Any(item => item.ProvenanceRef != "screenshot-capture-provenance.json"
            || item.NetworkDecisionRef != "screenshot-network-decisions.json"))
        {
            reasons.Add("screenshot-reference-mismatch");
        }

        if (!File.Exists(Path.Combine(root, "README.md")))
        {
            reasons.Add("readme-missing");
        }
    }

    private static void ValidateScreenshotComparisonLineage(
        CaptureSummary summary,
        ScreenshotCaptureProvenance provenance,
        HashSet<string> reasons)
    {
        foreach (var reason in CaptureRunLineageReasons(summary, provenance))
        {
            reasons.Add(reason);
        }
    }

    internal static IReadOnlyList<string> ValidateControlledRuns(
        CaptureSummary runASummary,
        ScreenshotCaptureProvenance runAProvenance,
        CaptureSummary runBSummary,
        ScreenshotCaptureProvenance runBProvenance)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in CaptureRunLineageReasons(runASummary, runAProvenance))
        {
            reasons.Add($"run-a:{reason}");
        }

        foreach (var reason in CaptureRunLineageReasons(runBSummary, runBProvenance))
        {
            reasons.Add($"run-b:{reason}");
        }

        ValidateControlledRunValues(
            runASummary.CaptureId,
            runASummary.StartedAtUtc,
            runAProvenance.CapturedAtUtc,
            runASummary.CompletedAtUtc,
            runBSummary.CaptureId,
            runBSummary.StartedAtUtc,
            runBProvenance.CapturedAtUtc,
            runBSummary.CompletedAtUtc,
            reasons);
        return reasons.Order(StringComparer.Ordinal).ToArray();
    }

    private static List<string> CaptureRunLineageReasons(
        CaptureSummary summary,
        ScreenshotCaptureProvenance provenance)
    {
        var reasons = new List<string>();
        if (summary.Status != CaptureFailureEvidence.CompleteStatus
            || summary.FailureReason is not null
            || summary.FailureStage is not null)
        {
            reasons.Add("capture-run-diagnostic");
        }

        if (summary.FailureAffectedUrl is { } affectedUrl
            && !IsSafelyRedactedUrl(affectedUrl))
        {
            reasons.Add("capture-failure-affected-url-invalid");
        }

        if (summary.FailureDetailReason is { } detailReason
            && (detailReason.Length > 128
                || !Regex.IsMatch(
                    detailReason,
                    "^[A-Za-z0-9:_-]+$",
                    RegexOptions.CultureInvariant)
                || SensitiveFailureFragments.Any(fragment =>
                    detailReason.Contains(
                        fragment,
                        StringComparison.OrdinalIgnoreCase))))
        {
            reasons.Add("capture-failure-detail-reason-invalid");
        }

        if (summary.CaptureId != provenance.CaptureId)
        {
            reasons.Add("capture-lineage-mismatch");
        }

        if (summary.SourceBaseUrl != CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri)
        {
            reasons.Add("source-origin-mismatch");
        }

        if (!summary.NoSubmit)
        {
            reasons.Add("no-submit-required");
        }

        if (string.IsNullOrWhiteSpace(summary.CaptureId)
            || summary.StartedAtUtc == default
            || summary.CompletedAtUtc == default
            || summary.CompletedAtUtc < summary.StartedAtUtc
            || summary.DurationSeconds < 0
            || provenance.CapturedAtUtc < summary.StartedAtUtc
            || provenance.CapturedAtUtc > summary.CompletedAtUtc)
        {
            reasons.Add("capture-summary-lineage-invalid");
        }

        if (string.IsNullOrWhiteSpace(provenance.CaptureId)
            || provenance.CapturedAtUtc == default)
        {
            reasons.Add("capture-provenance-lineage-invalid");
        }

        return reasons;
    }

    private static void ValidateControlledRunValues(
        string runACaptureId,
        DateTimeOffset runAStartedAtUtc,
        DateTimeOffset runACapturedAtUtc,
        DateTimeOffset runACompletedAtUtc,
        string runBCaptureId,
        DateTimeOffset runBStartedAtUtc,
        DateTimeOffset runBCapturedAtUtc,
        DateTimeOffset runBCompletedAtUtc,
        ICollection<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(runACaptureId)
            || string.IsNullOrWhiteSpace(runBCaptureId)
            || runACaptureId.Equals(runBCaptureId, StringComparison.Ordinal))
        {
            reasons.Add("controlled-run-capture-id-not-distinct");
        }

        if (runAStartedAtUtc == default
            || runACapturedAtUtc == default
            || runACompletedAtUtc == default
            || runBStartedAtUtc == default
            || runBCapturedAtUtc == default
            || runBCompletedAtUtc == default
            || runACapturedAtUtc < runAStartedAtUtc
            || runACompletedAtUtc < runACapturedAtUtc
            || runBCapturedAtUtc < runBStartedAtUtc
            || runBCompletedAtUtc < runBCapturedAtUtc)
        {
            reasons.Add("controlled-run-timestamp-order-invalid");
        }

        if (runBCapturedAtUtc <= runACapturedAtUtc)
        {
            reasons.Add("controlled-run-captured-at-not-after");
        }

        if (runBCompletedAtUtc <= runACompletedAtUtc)
        {
            reasons.Add("controlled-run-completed-at-not-after");
        }
    }

    private static async Task ValidateScreenshotsAsync(
        string root,
        IReadOnlyList<BaselineFileManifestEntry> files,
        CaptureSummary summary,
        ScreenshotRecord[] screenshots,
        IReadOnlyDictionary<(string Key, int Attempt), RequestLedgerSnapshot> ledgerSnapshots,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        var expected = CaptureProfile.Approved.ExpectedScreenshotKeys;
        var groups = screenshots.GroupBy(Key, StringComparer.Ordinal).ToArray();
        if (groups.Any(group => group.Count() != 1))
        {
            reasons.Add("screenshot-key-duplicate");
        }

        var actual = groups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            reasons.Add("screenshot-key-set-mismatch");
        }

        var captured = screenshots.Count(item => item.Status == "captured");
        var qualityPass = screenshots.Count(item => item.QualityStatus == "pass");
        if (summary.ScreenshotCount != screenshots.Length
            || summary.SuccessfulScreenshotCount != captured
            || summary.QualityPassScreenshotCount != qualityPass)
        {
            reasons.Add("screenshot-summary-count-mismatch");
        }

        if (screenshots.Length != 36 || captured != 36 || qualityPass != 36)
        {
            reasons.Add("screenshot-capture-or-quality-incomplete");
        }

        var savedPaths = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var screenshot in screenshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(screenshot);
            if (screenshot.Status != "captured"
                || screenshot.QualityStatus != "pass"
                || screenshot.QualityReasonCodes is null
                || screenshot.QualityReasonCodes.Count != 0)
            {
                reasons.Add($"screenshot-quality-failed:{key}");
            }

            if (screenshot.HorizontalOverflow
                || screenshot.DocumentScrollWidth > screenshot.ActualViewportWidth)
            {
                reasons.Add($"horizontal-overflow:{key}");
            }

            if (!CaptureProfile.Approved.Representatives.TryGetValue(
                    screenshot.TemplateKey,
                    out var representative)
                || !representative.AbsoluteUri.Equals(screenshot.Url, StringComparison.Ordinal)
                || CaptureProfile.Approved.Viewports.SingleOrDefault(
                    viewport => viewport.Name == screenshot.Viewport) is not { } viewport
                || viewport.Width != screenshot.Width
                || viewport.Height != screenshot.Height)
            {
                reasons.Add($"screenshot-approved-tuple-mismatch:{key}");
            }

            if (string.IsNullOrWhiteSpace(screenshot.SavedAs)
                || string.IsNullOrWhiteSpace(screenshot.Sha256))
            {
                reasons.Add($"screenshot-file-reference-missing:{key}");
                continue;
            }

            var relative = screenshot.SavedAs.Replace('\\', '/');
            if (!relative.StartsWith("screenshots/", StringComparison.Ordinal)
                || !savedPaths.Add(relative))
            {
                reasons.Add($"screenshot-file-reference-duplicate-or-invalid:{key}");
            }

            var path = ResolveChildPath(root, relative);
            if (!File.Exists(path))
            {
                reasons.Add($"screenshot-file-missing:{key}");
                continue;
            }

            if (new FileInfo(path).Length > PngArtifactInspector.MaximumPngBytes)
            {
                reasons.Add($"screenshot-png-too-large:{key}");
                continue;
            }

            PngArtifactInspection inspection;
            try
            {
                inspection = PngArtifactInspector.Inspect(
                    await File.ReadAllBytesAsync(path, cancellationToken));
            }
            catch (InvalidDataException)
            {
                reasons.Add($"screenshot-png-invalid:{key}");
                continue;
            }

            if (!string.Equals(inspection.Sha256, screenshot.Sha256, StringComparison.Ordinal)
                || inspection.Length != screenshot.PngBytes)
            {
                reasons.Add($"screenshot-hash-or-size-mismatch:{key}");
            }

            if (inspection.Width != screenshot.Width
                || inspection.Height != screenshot.Height
                || inspection.Width != screenshot.PngWidth
                || inspection.Height != screenshot.PngHeight)
            {
                reasons.Add($"screenshot-png-dimension-mismatch:{key}");
            }

            if (Math.Abs(inspection.ByteEntropy - screenshot.ByteEntropy) > 0.000_001)
            {
                reasons.Add($"screenshot-png-entropy-mismatch:{key}");
            }

            if (!hashes.TryGetValue(inspection.Sha256, out var hashKeys))
            {
                hashKeys = [];
                hashes.Add(inspection.Sha256, hashKeys);
            }

            hashKeys.Add(key);
            if (!ledgerSnapshots.TryGetValue((key, screenshot.AttemptCount), out var ledger))
            {
                reasons.Add($"network-ledger-missing:{key}:attempt:{screenshot.AttemptCount}");
                ledger = new(false, 0, 0, 0, ["network-ledger-missing"]);
            }

            var actualScreenshot = screenshot with
            {
                Sha256 = inspection.Sha256,
                PngBytes = inspection.Length,
                PngWidth = inspection.Width,
                PngHeight = inspection.Height,
                ByteEntropy = inspection.ByteEntropy
            };
            var recomputed = ScreenshotQualityEvaluator.Evaluate(new(
                screenshot.TemplateKey,
                screenshot.Width,
                screenshot.Height,
                actualScreenshot,
                ledger,
                screenshot.ControlledRunDonationFormCount));
            if (recomputed.QualityStatus != screenshot.QualityStatus
                || !recomputed.ReasonCodes.SequenceEqual(
                    screenshot.QualityReasonCodes ?? [],
                    StringComparer.Ordinal))
            {
                reasons.Add($"screenshot-quality-claim-mismatch:{key}");
            }
        }

        if (hashes.Any(pair => pair.Value.Count > 1))
        {
            reasons.Add("screenshot-png-reused-across-keys");
        }

        var actualScreenshotPaths = files
            .Select(file => file.RelativePath)
            .Where(relative => relative.StartsWith("screenshots/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualScreenshotPaths.SetEquals(savedPaths))
        {
            reasons.Add("screenshot-file-set-mismatch");
        }
    }

    private static void ValidateProvenance(
        ScreenshotCaptureProvenance provenance,
        IReadOnlyList<ScreenshotRecord> screenshots,
        HashSet<string> reasons)
    {
        if (provenance.Status != CaptureFailureEvidence.CompleteStatus
            || provenance.FailureReason is not null)
        {
            reasons.Add("capture-provenance-diagnostic");
        }

        foreach (var reason in ValidateBrowserIsolationProvenance(provenance))
        {
            reasons.Add(reason);
        }

        if (provenance.NetworkPolicyVersion != CaptureProfile.Approved.PolicyVersion
            || provenance.PrimaryOrigin != CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority)
            || provenance.PinnedDnsAnswers is null
            || provenance.ChromiumHostResolverRules is null
            || provenance.PinnedDnsAnswers.Count != CaptureProfile.Approved.StaticResources.Count + 1
            || provenance.ChromiumHostResolverRules.Count != provenance.PinnedDnsAnswers.Count + 1
            || provenance.ChromiumHostResolverRules[^1] != "MAP * ~NOTFOUND")
        {
            reasons.Add("trust-provenance-mismatch");
        }

        var originHost = CaptureProfile.Approved.PrimaryOrigin.Host;
        var expectedHostClasses = CaptureProfile.Approved.StaticResources
            .ToDictionary(
                rule => rule.Host,
                _ => nameof(TrustedHostClass.StaticResource),
                StringComparer.OrdinalIgnoreCase);
        expectedHostClasses.Add(
            originHost,
            nameof(TrustedHostClass.PrimaryOrigin));
        IReadOnlyList<string>? initialOriginAnswers = null;
        var pinnedDnsAnswers = provenance.PinnedDnsAnswers
            ?? new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
        pinnedDnsAnswers.TryGetValue(
            originHost,
            out initialOriginAnswers);
        var epochs = provenance.DnsContextEpochs ?? [];
        var expectedAssociations = screenshots
            .SelectMany(screenshot => Enumerable.Range(1, screenshot.AttemptCount)
                .Select(attempt => (Key(screenshot), Attempt: attempt)))
            .ToHashSet();
        var actualAssociations = epochs
            .Select(epoch => (epoch.CaptureKey, epoch.Attempt))
            .ToArray();
        var previousBindings = pinnedDnsAnswers
            .ToDictionary(
                pair => pair.Key,
                pair => new DnsPinBindingEvidence(
                    pair.Key,
                    expectedHostClasses[pair.Key],
                    pair.Value[0],
                    pair.Value,
                    CaptureIO.Sha256(
                        Encoding.UTF8.GetBytes(string.Join('\n', pair.Value))),
                    provenance.CapturedAtUtc,
                    false),
                StringComparer.OrdinalIgnoreCase);
        var epochDetailsInvalid = false;
        foreach (var epoch in epochs)
        {
            var bindingHosts = epoch.Bindings
                .Select(binding => binding.Host)
                .ToArray();
            if (epoch.CreatedAtUtc > epoch.PreNavigationValidatedAtUtc
                || epoch.PreNavigationValidatedAtUtc > epoch.PreScreenshotValidatedAtUtc
                || epoch.PreScreenshotValidatedAtUtc > epoch.PostScreenshotValidatedAtUtc
                || bindingHosts.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    != bindingHosts.Length
                || !bindingHosts.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(expectedHostClasses.Keys))
            {
                epochDetailsInvalid = true;
            }

            foreach (var binding in epoch.Bindings)
            {
                var orderedAnswers = binding.CompleteObservedPublicSet
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (!expectedHostClasses.TryGetValue(
                        binding.Host,
                        out var expectedHostClass)
                    || binding.HostClass != expectedHostClass
                    || binding.CompleteObservedPublicSet.Count == 0
                    || binding.CompleteObservedPublicSet
                        .Distinct(StringComparer.Ordinal)
                        .Count() != binding.CompleteObservedPublicSet.Count
                    || !binding.CompleteObservedPublicSet.SequenceEqual(
                        orderedAnswers,
                        StringComparer.Ordinal)
                    || orderedAnswers[0] != binding.SelectedAddress
                    || binding.AnswerSetSha256
                        != CaptureIO.Sha256(
                            Encoding.UTF8.GetBytes(string.Join('\n', orderedAnswers)))
                    || binding.CompleteObservedPublicSet.Any(address =>
                        !IPAddress.TryParse(address, out var parsed)
                        || !TrustedEndpointPolicy.IsPublicAddress(parsed)))
                {
                    epochDetailsInvalid = true;
                }

                var hasPrevious = previousBindings.TryGetValue(
                    binding.Host,
                    out var previous);
                var selectedPinChanged = hasPrevious
                    && previous!.SelectedAddress != binding.SelectedAddress;
                if (binding.Rotated != selectedPinChanged
                    || binding.Host.Equals(
                        originHost,
                        StringComparison.OrdinalIgnoreCase)
                        && (binding.Rotated
                            || initialOriginAnswers is null
                            || !binding.CompleteObservedPublicSet.SequenceEqual(
                                initialOriginAnswers,
                                StringComparer.OrdinalIgnoreCase)))
                {
                    epochDetailsInvalid = true;
                }

            }

            previousBindings = epoch.Bindings.ToDictionary(
                binding => binding.Host,
                StringComparer.OrdinalIgnoreCase);
        }

        if (epochs.Count == 0
            || epochs.Select(epoch => epoch.Epoch).Distinct().Count() != epochs.Count
            || !epochs.Select(epoch => epoch.Epoch).SequenceEqual(
                Enumerable.Range(1, epochs.Count).Select(value => (long)value))
            || actualAssociations.Distinct().Count() != actualAssociations.Length
            || !actualAssociations.ToHashSet().SetEquals(expectedAssociations)
            || epochs.Select(epoch => epoch.BrowserContextId)
                .Distinct(StringComparer.Ordinal).Count() != epochs.Count
            || epochDetailsInvalid
            || epochs.Any(epoch =>
                string.IsNullOrWhiteSpace(epoch.CaptureKey)
                || epoch.Attempt <= 0
                || string.IsNullOrWhiteSpace(epoch.BrowserInstanceId)
                || string.IsNullOrWhiteSpace(epoch.BrowserContextId)
                || epoch.PreNavigationValidatedAtUtc is null
                || epoch.PreScreenshotValidatedAtUtc is null
                || epoch.PostScreenshotValidatedAtUtc is null
                || epoch.Bindings.Count != expectedHostClasses.Count))
        {
            reasons.Add("dns-epoch-provenance-invalid");
        }

        if (!ValidateBrowserLaunchProvenance(provenance, epochs))
        {
            reasons.Add("browser-launch-provenance-invalid");
        }

        if (!ValidateBrowserLaunchChronology(provenance.BrowserLaunches))
        {
            reasons.Add("browser-launch-chronology-invalid");
        }
    }

    private static bool ValidateBrowserLaunchChronology(
        IReadOnlyList<BrowserLaunchEvidence> launches)
    {
        if (launches.Select(launch => launch.BrowserInstanceId)
            .Distinct(StringComparer.Ordinal).Count() != launches.Count)
        {
            return true;
        }

        var byId = launches.ToDictionary(
            launch => launch.BrowserInstanceId,
            StringComparer.Ordinal);
        foreach (var launch in launches)
        {
            if (launch.PreviousBrowserInstanceId is not { } predecessorId
                || !byId.TryGetValue(predecessorId, out var predecessor))
            {
                continue;
            }

            if (launch.LaunchedAtUtc < predecessor.LaunchedAtUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateBrowserLaunchProvenance(
        ScreenshotCaptureProvenance provenance,
        IReadOnlyList<DnsContextEpochEvidence> epochs)
    {
        var launches = provenance.BrowserLaunches ?? [];
        if (launches.Count == 0
            || launches.Select(launch => launch.BrowserInstanceId)
                .Distinct(StringComparer.Ordinal).Count() != launches.Count
            || launches.Any(launch =>
                string.IsNullOrWhiteSpace(launch.BrowserInstanceId)
                || !IsSha256(launch.ResolverMapSha256)
                || string.IsNullOrWhiteSpace(launch.Reason)
                || launch.Reason.Length > 128))
        {
            return false;
        }

        var bootstrap = launches[0];
        var bootstrapHash = TrustedEndpointPolicy.ComputeResolverMapSha256(
            provenance.ChromiumHostResolverRules);
        if (bootstrap.PreviousBrowserInstanceId is not null
            || bootstrap.Reason != "bootstrap"
            || bootstrap.ResolverMapSha256 != bootstrapHash
            || bootstrap.LaunchedAtUtc > provenance.CapturedAtUtc)
        {
            return false;
        }

        var launchById = launches.ToDictionary(
            launch => launch.BrowserInstanceId,
            StringComparer.Ordinal);
        var usedLaunchIds = new HashSet<string>(StringComparer.Ordinal)
        {
            bootstrap.BrowserInstanceId
        };
        var previousPins = provenance.PinnedDnsAnswers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value[0],
            StringComparer.OrdinalIgnoreCase);
        var previousBrowserInstanceId = bootstrap.BrowserInstanceId;
        foreach (var epoch in epochs)
        {
            var currentPins = epoch.Bindings.ToDictionary(
                binding => binding.Host,
                binding => binding.SelectedAddress,
                StringComparer.OrdinalIgnoreCase);
            if (!currentPins.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(previousPins.Keys)
                || !launchById.TryGetValue(epoch.BrowserInstanceId, out var launch))
            {
                return false;
            }

            var mapChanged = currentPins.Any(pair =>
                !previousPins.TryGetValue(pair.Key, out var previous)
                || previous != pair.Value);
            if (epoch.Bindings.Any(binding =>
                    binding.Rotated
                    != (previousPins.TryGetValue(binding.Host, out var previous)
                        && previous != binding.SelectedAddress)))
            {
                return false;
            }

            if (mapChanged == (epoch.BrowserInstanceId == previousBrowserInstanceId))
            {
                return false;
            }

            var resolverRules = TrustedEndpointPolicy.CreateChromiumHostResolverRules(
                currentPins.ToDictionary(
                    pair => pair.Key,
                    pair => IPAddress.Parse(pair.Value),
                    StringComparer.OrdinalIgnoreCase));
            if (launch.ResolverMapSha256
                    != TrustedEndpointPolicy.ComputeResolverMapSha256(resolverRules)
                || launch.LaunchedAtUtc > epoch.CreatedAtUtc)
            {
                return false;
            }

            if (mapChanged
                && (launch.PreviousBrowserInstanceId != previousBrowserInstanceId
                    || launch.Reason != "resolver-map-changed"))
            {
                return false;
            }

            usedLaunchIds.Add(launch.BrowserInstanceId);
            previousPins = currentPins;
            previousBrowserInstanceId = epoch.BrowserInstanceId;
        }

        return usedLaunchIds.SetEquals(launchById.Keys);
    }

    internal static IReadOnlyList<string> ValidateBrowserIsolationProvenance(
        ScreenshotCaptureProvenance provenance)
    {
        var reasons = new List<string>();
        var identity = CaptureProfile.Approved.GetCurrentBrowserIdentity();
        if (provenance.PlaywrightPackageVersion != CaptureProfile.PlaywrightVersion
            || provenance.ChromiumRevision != CaptureProfile.ChromiumRevision
            || provenance.ChromiumVersion != CaptureProfile.ChromiumVersion)
        {
            reasons.Add("browser-version-provenance-mismatch");
        }

        if (provenance.ExpectedExecutableSha256 != identity.ExecutableSha256
            || provenance.ActualExecutableSha256 != identity.ExecutableSha256)
        {
            reasons.Add("browser-hash-provenance-mismatch");
        }

        if (!provenance.ChromiumSandbox
            || provenance.ProcessElevated
            || provenance.CookiesUsed
            || provenance.StorageStateUsed
            || provenance.PermissionsGranted
            || provenance.ProxyUsed
            || provenance.CredentialsUsed)
        {
            reasons.Add("browser-isolation-provenance-failed");
        }

        if (provenance.ChromiumExecutable
                != PlaywrightScreenshotCapture.BrowserExecutableIdentity
            || !provenance.BrowserExecutableOutsideWorkspace)
        {
            reasons.Add("browser-executable-provenance-mismatch");
        }

        if (provenance.ChildEnvironmentKeys is null
            || provenance.ChildEnvironmentKeys.Count
                != ExpectedChildEnvironmentKeys.Count
            || !provenance.ChildEnvironmentKeys
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(ExpectedChildEnvironmentKeys)
            || !provenance.ChildEnvironmentSanitized)
        {
            reasons.Add("browser-environment-provenance-failed");
        }

        if (provenance.BrowserWorkingDirectory
                != PlaywrightScreenshotCapture.BrowserWorkingDirectoryAttestation
            || !provenance.BrowserWorkingDirectoryOutsideWorkspace
            || !provenance.BrowserWorkingDirectoryEmpty)
        {
            reasons.Add("browser-working-directory-provenance-failed");
        }

        return reasons;
    }

    private static void ValidateNetworkPolicy(
        ScreenshotNetworkPolicyDocument policy,
        ScreenshotCaptureProvenance provenance,
        HashSet<string> reasons)
    {
        if (policy.Status != CaptureFailureEvidence.CompleteStatus
            || policy.FailureReason is not null)
        {
            reasons.Add("screenshot-network-policy-diagnostic");
        }

        var expectedHosts = CaptureProfile.Approved.StaticResources
            .Select(rule => rule.Host)
            .Append(CaptureProfile.Approved.PrimaryOrigin.Host)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (policy.PolicyVersion != CaptureProfile.Approved.PolicyVersion
            || !policy.AllowedMethods.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(ScreenshotNetworkPolicy.AllowedMethods)
            || !policy.AllowedOrigins.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals([CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority)])
            || !policy.AllowedStaticHosts.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(CaptureProfile.Approved.StaticResources.Select(rule => rule.Host))
            || !policy.AllowedStaticResourceTypes.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(ScreenshotNetworkPolicy.AllowedStaticResourceTypes)
            || !policy.BlockedCapabilities.ToHashSet(StringComparer.Ordinal)
                .SetEquals(ScreenshotNetworkPolicy.BlockedCapabilities)
            || policy.NavigationAttempts != PlaywrightScreenshotCapture.MaximumNavigationAttempts
            || policy.NavigationAttemptTimeoutSeconds
                != PlaywrightScreenshotCapture.NavigationAttemptTimeoutMilliseconds / 1000
            || policy.ReadinessTimeoutSeconds
                != (int)PlaywrightScreenshotCapture.MaximumReadinessDuration.TotalSeconds
            || policy.CaptureTimeoutSeconds
                != (int)PlaywrightScreenshotCapture.MaximumCaptureDuration.TotalSeconds
            || policy.InterCaptureDelayMilliseconds
                != PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds
            || string.IsNullOrWhiteSpace(policy.Enforcement)
            || !policy.ChromiumHostResolverRules.SequenceEqual(
                provenance.ChromiumHostResolverRules,
                StringComparer.Ordinal)
            || !DnsAnswersEqual(policy.PinnedDnsAnswers, provenance.PinnedDnsAnswers)
            || !policy.PinnedDnsAnswers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(expectedHosts))
        {
            reasons.Add("network-policy-canonical-mismatch");
        }

        foreach (var host in policy.PinnedDnsAnswers)
        {
            if (host.Value.Count == 0
                || host.Value.Any(value =>
                    !System.Net.IPAddress.TryParse(value, out var address)
                    || !TrustedEndpointPolicy.IsPublicAddress(address)))
            {
                reasons.Add($"network-policy-pinned-address-invalid:{host.Key}");
            }
        }

        var mappings = policy.PinnedDnsAnswers
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        if (policy.ChromiumHostResolverRules.Count != mappings.Length + 1
            || policy.ChromiumHostResolverRules[^1] != "MAP * ~NOTFOUND")
        {
            reasons.Add("network-policy-resolver-rules-invalid");
            return;
        }

        for (var index = 0; index < mappings.Length; index++)
        {
            var parts = policy.ChromiumHostResolverRules[index]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var expectedAddress = mappings[index].Value.Count == 0
                ? null
                : mappings[index].Value[0];
            if (parts.Length != 3
                || parts[0] != "MAP"
                || !parts[1].Equals(mappings[index].Key, StringComparison.OrdinalIgnoreCase)
                || expectedAddress is null
                || !System.Net.IPAddress.TryParse(
                    parts[2].Trim('[', ']'),
                    out var mappedAddress)
                || !System.Net.IPAddress.TryParse(expectedAddress, out var expectedIp)
                || !mappedAddress.Equals(expectedIp)
                || !TrustedEndpointPolicy.IsPublicAddress(mappedAddress))
            {
                reasons.Add($"network-policy-resolver-rule-invalid:{mappings[index].Key}");
            }
        }
    }

    private static bool DnsAnswersEqual(
        IReadOnlyDictionary<string, IReadOnlyList<string>> left,
        IReadOnlyDictionary<string, IReadOnlyList<string>> right)
    {
        if (!left.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.Keys))
        {
            return false;
        }

        return left.All(pair =>
            right.TryGetValue(pair.Key, out var values)
            && pair.Value.SequenceEqual(values, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IReadOnlyDictionary<(string Key, int Attempt), RequestLedgerSnapshot>>
        ValidateDecisionLogAsync(
        string root,
        ScreenshotRecord[] screenshots,
        ScreenshotCaptureProvenance provenance,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        var decisions = await ReadJsonAsync<ScreenshotNetworkDecision[]>(
            Path.Combine(root, "screenshot-network-decisions.json"),
            cancellationToken);
        var decisionRows = decisions
            .Where(item => item.EventType is "request" or "capability")
            .ToArray();
        if (decisionRows.Length == 0)
        {
            reasons.Add("network-decision-log-empty");
        }

        var epochs = (provenance.DnsContextEpochs ?? [])
            .ToDictionary(epoch => epoch.Epoch);
        foreach (var decision in decisions)
        {
            if (decision.DnsEpoch is null
                || decision.BrowserInstanceId is null
                || decision.BrowserContextId is null
                || !epochs.TryGetValue(decision.DnsEpoch.Value, out var epoch)
                || epoch.CaptureKey != decision.CaptureKey
                || epoch.Attempt != decision.Attempt
                || epoch.BrowserInstanceId != decision.BrowserInstanceId
                || epoch.BrowserContextId != decision.BrowserContextId)
            {
                reasons.Add($"network-dns-epoch-association-invalid:{decision.RequestId}");
            }
        }

        var screenshotByKey = screenshots
            .GroupBy(Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var requestGroups = decisions.GroupBy(
            item => (item.CaptureKey, item.Attempt, item.RequestId));
        var snapshots = new Dictionary<(string Key, int Attempt), RequestLedgerSnapshot>();
        foreach (var group in requestGroups)
        {
            var decisionEvents = group
                .Where(item => item.EventType is "request" or "capability")
                .ToArray();
            var terminals = group
                .Where(item =>
                    item.EventType is "response" or "failure" or "lifecycle"
                        or "capability-terminal")
                .ToArray();
            if (decisionEvents.Length != 1)
            {
                reasons.Add($"network-decision-count:{group.Key.CaptureKey}:{group.Key.Attempt}:{group.Key.RequestId}");
            }

            if (terminals.Length != 1)
            {
                reasons.Add($"network-terminal-count:{group.Key.CaptureKey}:{group.Key.Attempt}:{group.Key.RequestId}");
            }

            if (decisionEvents.Length == 1 && terminals.Length == 1)
            {
                var decision = decisionEvents[0];
                var terminal = terminals[0];
                if (decision.CaptureKey != terminal.CaptureKey
                    || decision.Attempt != terminal.Attempt
                    || decision.Method != terminal.Method
                    || decision.RedactedUrl != terminal.RedactedUrl
                    || decision.ResourceType != terminal.ResourceType
                    || decision.IsNavigationRequest != terminal.IsNavigationRequest
                    || decision.Decision != terminal.Decision
                    || decision.PolicyVersion != terminal.PolicyVersion
                    || decision.IsMainFrame != terminal.IsMainFrame
                    || decision.PinnedAddress != terminal.PinnedAddress
                    || decision.Decision == "block" && terminal.EventType != "failure")
                {
                    reasons.Add($"network-terminal-correlation-mismatch:{group.Key.RequestId}");
                }

                if (decision.ResponseStatus is not null
                    || decision.Failure is not null)
                {
                    reasons.Add($"network-decision-terminal-fields-present:{group.Key.RequestId}");
                }

                if (terminal.EventType == "response"
                    && (decision.Decision != "allow"
                        || terminal.ResponseStatus is not >= 100 or > 599
                        || terminal.Failure is not null
                        || terminal.ReasonCode != "response-complete"))
                {
                    reasons.Add($"network-response-terminal-invalid:{group.Key.RequestId}");
                }

                if (terminal.EventType == "failure"
                    && (terminal.ResponseStatus is not null
                        || string.IsNullOrWhiteSpace(terminal.Failure)
                        || decision.Decision == "block"
                            && terminal.ReasonCode != decision.ReasonCode))
                {
                    reasons.Add($"network-failure-terminal-invalid:{group.Key.RequestId}");
                }

                if (terminal.EventType == "lifecycle"
                    && (decision.Decision != "allow"
                        || terminal.ResponseStatus is not null
                        || terminal.ReasonCode
                            != "attempt-aborted-before-browser-terminal"
                        || terminal.Failure != "capture-attempt-aborted"))
                {
                    reasons.Add(
                        $"network-attempt-abort-terminal-invalid:{group.Key.RequestId}");
                }

                if (terminal.EventType == "capability-terminal"
                    && (decision.EventType != "capability"
                        || decision.Decision != "allow"
                        || decision.Method != "COOKIE-READ"
                        || decision.ReasonCode != "cookie-read-empty-context"
                        || terminal.ReasonCode != "cookie-read-empty-context"
                        || terminal.ResponseStatus is not null
                        || terminal.Failure is not null))
                {
                    reasons.Add(
                        $"network-capability-terminal-invalid:{group.Key.RequestId}");
                }
            }
        }

        foreach (var decision in decisions)
        {
            if (decision.EventType is not (
                    "request"
                    or "capability"
                    or "response"
                    or "failure"
                    or "lifecycle"
                    or "capability-terminal")
                || decision.PolicyVersion != CaptureProfile.Approved.PolicyVersion
                || decision.Decision is not ("allow" or "block")
                || string.IsNullOrWhiteSpace(decision.ReasonCode))
            {
                reasons.Add("network-decision-unclassified");
            }

            if (!IsSafelyRedactedUrl(decision.RedactedUrl))
            {
                reasons.Add("network-log-sensitive-query");
            }

            if (decision.Failure is { } failure
                && (failure.Contains("://", StringComparison.Ordinal)
                    || failure.Contains('\r')
                    || failure.Contains('\n')
                    || SensitiveFailureFragments
                        .Any(fragment => failure.Contains(fragment, StringComparison.OrdinalIgnoreCase))))
            {
                reasons.Add("network-log-sensitive-failure");
            }

            if (!screenshotByKey.TryGetValue(decision.CaptureKey, out var screenshot)
                || decision.Attempt <= 0
                || decision.Attempt > screenshot.AttemptCount)
            {
                reasons.Add($"network-decision-outside-capture-attempt:{decision.CaptureKey}:{decision.Attempt}");
            }

            if (decision.EventType is "request" or "capability")
            {
                ValidateDecisionSemantics(decision, provenance, reasons);
            }
        }

        foreach (var screenshot in screenshotByKey)
        {
            if (screenshot.Value.AttemptCount <= 0)
            {
                reasons.Add($"network-attempt-count-invalid:{screenshot.Key}");
                continue;
            }

            var loggedAttempts = decisions
                .Where(decision => decision.CaptureKey == screenshot.Key)
                .Select(decision => decision.Attempt)
                .Distinct()
                .Order()
                .ToArray();
            var expectedAttempts = Enumerable.Range(
                    1,
                    screenshot.Value.AttemptCount)
                .ToArray();
            if (!loggedAttempts.SequenceEqual(expectedAttempts))
            {
                reasons.Add($"network-attempt-history-mismatch:{screenshot.Key}");
            }

            var attemptReasons = screenshot.Value.AttemptReasonCodes;
            if (attemptReasons is null
                || attemptReasons.Any(string.IsNullOrWhiteSpace)
                || attemptReasons.Distinct(StringComparer.Ordinal).Count()
                    != attemptReasons.Count
                || screenshot.Value.Status == "captured"
                    && screenshot.Value.AttemptCount == 1
                    && attemptReasons.Count != 0
                || screenshot.Value.AttemptCount > 1
                    && attemptReasons.Count == 0)
            {
                reasons.Add($"network-attempt-reasons-invalid:{screenshot.Key}");
            }

            for (var attempt = 1; attempt <= screenshot.Value.AttemptCount; attempt++)
            {
                var groups = requestGroups
                    .Where(group => group.Key.CaptureKey == screenshot.Key
                        && group.Key.Attempt == attempt)
                    .ToArray();
                if (groups.Length == 0)
                {
                    reasons.Add($"network-attempt-ledger-missing:{screenshot.Key}:attempt:{attempt}");
                    snapshots[(screenshot.Key, attempt)] =
                        new(false, 0, 0, 0, ["network-attempt-ledger-missing"]);
                    continue;
                }

                var decisionCount = groups.Count(group =>
                    group.Count(item => item.EventType is "request" or "capability") == 1);
                var terminalCount = groups.Count(group =>
                    group.Count(item =>
                        item.EventType is "response"
                            or "failure"
                            or "lifecycle"
                            or "capability-terminal") == 1);
                var passed = decisionCount == groups.Length
                    && terminalCount == groups.Length
                    && !reasons.Any(reason =>
                        reason.EndsWith(
                            $":{groups[0].Key.RequestId}",
                            StringComparison.Ordinal));
                snapshots[(screenshot.Key, attempt)] = new(
                    passed,
                    decisionCount,
                    terminalCount,
                    0,
                    passed ? [] : ["network-ledger-incomplete"]);
            }

            var finalAttempt = screenshot.Value.AttemptCount;
            var expectedUrl = EvidenceSanitizer.RedactUrl(screenshot.Value.Url);
            var successfulMainFrame = decisions.Any(decision =>
                decision.CaptureKey == screenshot.Key
                && decision.Attempt == finalAttempt
                && decision.EventType == "request"
                && decision.IsMainFrame
                && decision.IsNavigationRequest
                && decision.ResourceType.Equals("document", StringComparison.OrdinalIgnoreCase)
                && decision.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                && decision.Decision == "allow"
                && decision.RedactedUrl == expectedUrl
                && decisions.Any(terminal =>
                    terminal.CaptureKey == decision.CaptureKey
                    && terminal.Attempt == decision.Attempt
                    && terminal.RequestId == decision.RequestId
                    && terminal.EventType == "response"
                    && terminal.ResponseStatus is >= 200 and < 400));
            if (!successfulMainFrame)
            {
                reasons.Add($"network-main-frame-success-missing:{screenshot.Key}");
            }
        }

        return snapshots;
    }

    private static void ValidateDecisionSemantics(
        ScreenshotNetworkDecision decision,
        ScreenshotCaptureProvenance provenance,
        HashSet<string> reasons)
    {
        if (decision.EventType == "capability")
        {
            var capability = decision.Method.ToLowerInvariant();
            var knownCapabilities = new[]
            {
                "websocket", "eventsource", "webrtc", "webtransport", "direct-sockets",
                "payment-request", "sendbeacon", "serviceworker", "form-submit",
                "form-request-submit", "cookie-read", "cookie-write", "download", "popup",
                "unknown"
            };
            var allowedEmptyCookieRead =
                capability == "cookie-read"
                && decision.Decision == "allow"
                && decision.ReasonCode == "cookie-read-empty-context";
            var expectedBlockedReason = capability == "cookie-write"
                ? "capability-cookie-write-blocked"
                : $"capability-{capability}-blocked";
            if ((!allowedEmptyCookieRead
                    && (decision.Decision != "block"
                        || capability != "unknown"
                            && decision.ReasonCode != expectedBlockedReason))
                || decision.ResourceType != "capability"
                || !knownCapabilities.Contains(capability, StringComparer.Ordinal))
            {
                reasons.Add($"network-capability-decision-invalid:{decision.RequestId}");
            }

            return;
        }

        if (!Uri.TryCreate(decision.RedactedUrl, UriKind.Absolute, out var uri))
        {
            reasons.Add($"network-request-url-invalid:{decision.RequestId}");
            return;
        }

        var expected = ClassifyCanonicalRequest(decision, uri);
        var lifecycleBlocks = new HashSet<string>(StringComparer.Ordinal)
        {
            "unexpected-initial-navigation",
            "post-load-document-navigation",
            "cookie-request-blocked"
        };
        if (expected.Allowed)
        {
            if (decision.Decision == "allow")
            {
                if (decision.ReasonCode != expected.ReasonCode)
                {
                    reasons.Add($"network-request-reason-invalid:{decision.RequestId}");
                }
            }
            else if (!lifecycleBlocks.Contains(decision.ReasonCode))
            {
                reasons.Add($"network-request-decision-invalid:{decision.RequestId}");
            }
        }
        else if (decision.Decision != "block"
                 || decision.ReasonCode != expected.ReasonCode)
        {
            reasons.Add($"network-request-decision-invalid:{decision.RequestId}");
        }

        if (decision.Decision == "allow"
            || decision.Decision == "block"
                && lifecycleBlocks.Contains(decision.ReasonCode))
        {
            if (decision.PinnedAddress is null
                || !System.Net.IPAddress.TryParse(decision.PinnedAddress, out var pinned)
                || !TrustedEndpointPolicy.IsPublicAddress(pinned)
                || decision.DnsEpoch is null
                || provenance.DnsContextEpochs
                    .SingleOrDefault(epoch => epoch.Epoch == decision.DnsEpoch)
                    ?.Bindings.SingleOrDefault(binding =>
                        binding.Host.Equals(
                            uri.Host,
                            StringComparison.OrdinalIgnoreCase))
                    ?.SelectedAddress != decision.PinnedAddress)
            {
                reasons.Add($"network-request-pinned-address-invalid:{decision.RequestId}");
            }
        }
    }

    private static (bool Allowed, string ReasonCode) ClassifyCanonicalRequest(
        ScreenshotNetworkDecision decision,
        Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return (false, "credentials-not-allowed");
        }

        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            return (false, "ip-literal-not-allowed");
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "https-required");
        }

        if (uri.Port != 443)
        {
            return (false, "port-not-allowed");
        }

        var primaryOrigin = uri.Host.Equals(
            CaptureProfile.Approved.PrimaryOrigin.Host,
            StringComparison.OrdinalIgnoreCase);
        var staticRule = CaptureProfile.Approved.StaticResources.SingleOrDefault(rule =>
            rule.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase));
        if (!primaryOrigin && staticRule is null)
        {
            return (false, "host-not-approved");
        }

        if (!ScreenshotNetworkPolicy.AllowedMethods.Contains(
                decision.Method,
                StringComparer.OrdinalIgnoreCase))
        {
            return (false, "method-not-allowed");
        }

        if (EvidenceSanitizer.HasSensitiveQueryParameter(uri))
        {
            return (false, "sensitive-query-parameter");
        }

        if (primaryOrigin)
        {
            return EndpointSafetyClassifier.IsSensitiveEndpoint(uri)
                ? (false, "sensitive-endpoint-blocked")
                : (true, "primary-origin-get-head");
        }

        if (decision.IsNavigationRequest
            || decision.ResourceType.Equals("document", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "outside-origin-navigation");
        }

        return staticRule!.ResourceTypes.Contains(decision.ResourceType)
            ? (true, "approved-static-resource")
            : (false, "static-resource-type-not-approved");
    }

    private static async Task ValidateManifestsAsync(
        string root,
        IReadOnlyList<BaselineFileManifestEntry> files,
        CaptureSummary summary,
        RouteManifest routeManifest,
        ImportManifest importManifest,
        HttpRecord[] httpRecords,
        MetadataRecord[] metadata,
        AssetRecord[] assets,
        NavigationItem[] navigation,
        FormsWidgetsDocument forms,
        ReligiousContentRecord[] religiousContent,
        ResidualRisk[] risks,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        await ValidateFrozenManifestSchemasAsync(root, reasons, cancellationToken);

        if (routeManifest.SchemaVersion != EvidenceSchemaVersion
            || routeManifest.CaptureId != summary.CaptureId
            || routeManifest.SourceBaseUrl != summary.SourceBaseUrl
            || routeManifest.CapturedAtUtc != summary.StartedAtUtc
            || routeManifest.Routes is null)
        {
            reasons.Add("route-manifest-lineage-mismatch");
        }

        if (importManifest.SchemaVersion != EvidenceSchemaVersion
            || importManifest.SourceVersion != summary.CaptureId
            || importManifest.Source != summary.SourceBaseUrl
            || importManifest.CapturedAtUtc != summary.StartedAtUtc
            || string.IsNullOrWhiteSpace(importManifest.RightsProfile)
            || (string.IsNullOrWhiteSpace(importManifest.Mode)
                    ? "dry-run"
                    : importManifest.Mode) != "dry-run"
            || importManifest.Candidates is null)
        {
            reasons.Add("import-manifest-lineage-mismatch");
        }

        ValidateRouteCountDiagnostics(
            summary,
            routeManifest,
            httpRecords,
            risks,
            reasons);

        if (summary.RouteCount != httpRecords.Length
            || summary.SuccessfulRouteCount != httpRecords.Count(item => item.Status is not null)
            || summary.RedirectCount != httpRecords.Count(item => item.Redirects.Count > 0)
            || summary.MetadataCount != metadata.Length
            || summary.AssetCount != assets.Length
            || summary.DownloadedAssetCount != assets.Count(item => item.Sha256 is not null)
            || summary.FormCount != forms.Forms.Count
            || summary.DynamicRegionCount != forms.DynamicRegions.Count
            || summary.ResidualRiskCount != risks.Length)
        {
            reasons.Add("artifact-summary-count-mismatch");
        }

        foreach (var record in httpRecords.Where(record => record.Status is null))
        {
            reasons.Add($"http-route-capture-incomplete:{record.Url}");
        }

        if (!forms.NoSubmit
            || forms.Forms.Any(form => form.SubmissionAttempted)
            || navigation.Any(item => string.IsNullOrWhiteSpace(item.NavigationKey))
            || religiousContent.Any(item =>
                string.IsNullOrWhiteSpace(item.Route)
                || string.IsNullOrWhiteSpace(item.SourceChecksum)
                || string.IsNullOrWhiteSpace(item.FixtureChecksum)))
        {
            reasons.Add("artifact-content-invalid");
        }

        var assetKeys = assets
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        if ((routeManifest.Routes ?? [])
            .GroupBy(route => route.RouteId, StringComparer.Ordinal)
            .Any(group => group.Count() != 1))
        {
            reasons.Add("route-manifest-duplicate-route-id");
        }

        foreach (var route in routeManifest.Routes ?? [])
        {
            if (string.IsNullOrWhiteSpace(route.RouteId)
                || string.IsNullOrWhiteSpace(route.LegacyPath)
                || string.IsNullOrWhiteSpace(route.CanonicalPath)
                || string.IsNullOrWhiteSpace(route.TemplateKey)
                || route.AssetKeys is null
                || route.DynamicRegionKeys is null
                || route.EvidenceRefs is null
                || route.AssetKeys.Any(key => !assetKeys.Contains(key))
                || route.EvidenceRefs.Any(reference =>
                    string.IsNullOrWhiteSpace(reference)
                    || !File.Exists(ResolveChildPath(root, reference))))
            {
                reasons.Add($"route-manifest-entry-invalid:{route.RouteId}");
            }
        }

        ValidateRouteManifestSemantics(
            root,
            routeManifest.Routes ?? [],
            httpRecords,
            metadata,
            assets,
            forms,
            reasons);

        var candidateKeys = (importManifest.Candidates ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceKey))
            .Select(item => item.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in importManifest.Candidates ?? [])
        {
            if (string.IsNullOrWhiteSpace(candidate.SourceKey)
                || string.IsNullOrWhiteSpace(candidate.SourceVersion)
                || candidate.SourceVersion != summary.CaptureId
                || !IsSha256(candidate.SourceChecksum)
                || string.IsNullOrWhiteSpace(candidate.Kind)
                || string.IsNullOrWhiteSpace(candidate.TargetKey)
                || string.IsNullOrWhiteSpace(candidate.PayloadRef)
                || !File.Exists(ResolveChildPath(root, candidate.PayloadRef))
                || candidate.MediaRefs is null
                || candidate.DependencyKeys is null
                || candidate.MediaRefs.Any(reference => !candidateKeys.Contains(reference))
                || string.IsNullOrWhiteSpace(candidate.Decision))
            {
                reasons.Add($"import-manifest-entry-invalid:{candidate.SourceKey}");
            }
        }
        ValidateImportCandidateSemantics(
            importManifest.Candidates ?? [],
            summary,
            httpRecords,
            metadata,
            assets,
            navigation,
            forms,
            reasons);
        ValidateImportCandidateMediaRefs(
            importManifest.Candidates ?? [],
            assets,
            reasons);

        await ValidateImportCandidateChecksumsAsync(
            root,
            summary,
            importManifest.Candidates ?? [],
            httpRecords,
            metadata,
            assets,
            navigation,
            forms,
            reasons,
            cancellationToken);
        await ValidateReferencedFileGraphAsync(
            root,
            files,
            summary,
            httpRecords,
            assets,
            routeManifest,
            importManifest,
            reasons,
            cancellationToken);
    }

    private static void ValidateRouteCountDiagnostics(
        CaptureSummary summary,
        RouteManifest routeManifest,
        HttpRecord[] httpRecords,
        ResidualRisk[] risks,
        HashSet<string> reasons)
    {
        var parsedRecords = httpRecords
            .Select(record => (
                Record: record,
                Parsed: Uri.TryCreate(
                    record.Url,
                    UriKind.Absolute,
                    out var uri),
                Uri: uri))
            .Where(item => item.Parsed && item.Uri is not null)
            .ToArray();
        var queryEndpointCount = parsedRecords.Count(item =>
            !string.IsNullOrEmpty(item.Uri!.Query));
        var queryFreeDiscoveredPaths = parsedRecords
            .Where(item => string.IsNullOrEmpty(item.Uri!.Query))
            .Select(item => NormalizeManifestPath(item.Uri!))
            .ToHashSet(StringComparer.Ordinal);
        var manifestPaths = (routeManifest.Routes ?? [])
            .Select(route => route.LegacyPath)
            .ToHashSet(StringComparer.Ordinal);
        var representedDiscoveredCount = queryFreeDiscoveredPaths.Count(
            manifestPaths.Contains);
        var standaloneManifestPathCount =
            (routeManifest.Routes?.Count ?? 0) - representedDiscoveredCount;
        if (representedDiscoveredCount != queryFreeDiscoveredPaths.Count
            || summary.RouteCount
                - (routeManifest.Routes?.Count ?? 0)
                + standaloneManifestPathCount
                != queryEndpointCount)
        {
            reasons.Add("route-count-delta-not-query-only");
        }

        if (queryEndpointCount > 0
            && !risks.Any(risk =>
                risk.RiskId == "RISK-QUERY-ROUTE-SCHEMA"))
        {
            reasons.Add("query-route-schema-risk-missing");
        }

        var diagnosticCounts =
            new int?[]
            {
                summary.DiscoveredUrlCount,
                summary.ManifestPathCount,
                summary.QueryEndpointExcludedByFrozenSchemaCount
            };
        if (diagnosticCounts.All(value => value is null))
        {
            return;
        }

        if (diagnosticCounts.Any(value => value is null))
        {
            reasons.Add("route-count-diagnostics-incomplete");
            return;
        }

        var discoveredUrlCount = summary.DiscoveredUrlCount!.Value;
        var manifestPathCount = summary.ManifestPathCount!.Value;
        var excludedQueryCount =
            summary.QueryEndpointExcludedByFrozenSchemaCount!.Value;
        if (discoveredUrlCount != summary.RouteCount
            || manifestPathCount != (routeManifest.Routes?.Count ?? 0)
            || excludedQueryCount != queryEndpointCount)
        {
            reasons.Add("route-count-diagnostics-mismatch");
        }

        if (discoveredUrlCount
            - manifestPathCount
            + standaloneManifestPathCount
            != queryEndpointCount
            || excludedQueryCount != queryEndpointCount)
        {
            reasons.Add("route-count-delta-not-query-only");
        }
    }

    private static void ValidateRouteManifestSemantics(
        string root,
        IReadOnlyList<RouteEntry> routes,
        HttpRecord[] httpRecords,
        MetadataRecord[] metadata,
        AssetRecord[] assets,
        FormsWidgetsDocument forms,
        HashSet<string> reasons)
    {
        var expectedRoutes = BuildExpectedRouteEntries(
            root,
            httpRecords,
            metadata,
            assets,
            forms,
            reasons);
        var expectedGroups = expectedRoutes
            .GroupBy(route => route.RouteId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var actualRouteIds = routes
            .Select(route => route.RouteId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var route in routes)
        {
            if (!expectedGroups.TryGetValue(
                    route.RouteId,
                    out var matchingExpected)
                || matchingExpected.Length != 1
                || !RouteEntriesMatch(route, matchingExpected[0]))
            {
                reasons.Add($"route-manifest-semantic-mismatch:{route.RouteId}");
            }
        }

        foreach (var missing in expectedRoutes
            .Where(route => !actualRouteIds.Contains(route.RouteId))
            .Select(route => route.RouteId)
            .Distinct(StringComparer.Ordinal))
        {
            reasons.Add($"route-manifest-semantic-mismatch:{missing}");
        }
    }

    private static RouteEntry[] BuildExpectedRouteEntries(
        string root,
        HttpRecord[] httpRecords,
        MetadataRecord[] metadata,
        AssetRecord[] assets,
        FormsWidgetsDocument forms,
        HashSet<string> reasons)
    {
        var expected = new List<RouteEntry>();
        var retainedSitemapUrls = BuildRetainedSitemapUrls(
            root,
            httpRecords,
            reasons);

        foreach (var record in httpRecords
            .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(
                    record.Url,
                    UriKind.Absolute,
                    out var uri)
                || !string.IsNullOrEmpty(uri.Query))
            {
                continue;
            }

            var matchingMetadata = metadata
                .Where(item =>
                    item.Url.Equals(record.Url, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingMetadata.Length > 1)
            {
                continue;
            }

            var legacyPath = NormalizeManifestPath(uri);
            var recordMetadata = matchingMetadata.SingleOrDefault();
            var redirect = record.Redirects.Count == 0
                ? null
                : record.Redirects[0];
            var expectedStatus = BaselineCaptureService.GetExpectedRouteStatus(
                record,
                redirect);
            var expectedRedirectTarget = expectedStatus is 301 or 308
                && record.FinalUrl is not null
                && Uri.TryCreate(record.FinalUrl, UriKind.Absolute, out var finalUri)
                    ? NormalizeManifestPath(finalUri)
                    : null;
            var expectedAssets = assets
                .Where(asset => asset.SourcePage.Equals(
                    record.Url,
                    StringComparison.OrdinalIgnoreCase))
                .Select(asset => asset.Key)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expectedRegions = forms.DynamicRegions
                .Where(region => region.SourcePage == record.Url)
                .Select(region => region.RegionKey)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expectedEvidence = new[]
                {
                    record.SavedAs ?? "http-inventory.json",
                    "http-inventory.json",
                    recordMetadata is null
                        ? "capture-summary.json"
                        : "metadata-inventory.json"
                }
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            expected.Add(new RouteEntry(
                CaptureIO.StableKey("route", legacyPath),
                legacyPath,
                legacyPath,
                expectedStatus,
                expectedRedirectTarget,
                BaselineCaptureService.ClassifyTemplate(uri.AbsolutePath),
                record.Sha256 is null
                    ? null
                    : CaptureIO.StableKey("content", legacyPath),
                !string.Equals(
                    recordMetadata?.Robots,
                    "noindex",
                    StringComparison.OrdinalIgnoreCase),
                retainedSitemapUrls.Contains(NormalizeEvidenceUrl(uri)),
                recordMetadata is null
                    ? null
                    : CaptureIO.StableKey("metadata", legacyPath),
                expectedAssets,
                expectedRegions,
                expectedEvidence,
                record.Sha256));
        }

        var expectedPaths = expected
            .Select(route => route.LegacyPath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var asset in assets
            .Where(asset =>
                asset.Kind != "rejected"
                && asset.Status is >= 200 and < 300
                && Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
                && uri.AbsolutePath.StartsWith(
                    "/wp-content/uploads/",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.Url, StringComparer.OrdinalIgnoreCase))
        {
            var uri = new Uri(asset.Url);
            var legacyPath = NormalizeManifestPath(uri);
            if (!expectedPaths.Add(legacyPath))
            {
                continue;
            }

            expected.Add(new RouteEntry(
                CaptureIO.StableKey("route", legacyPath),
                legacyPath,
                legacyPath,
                200,
                null,
                "asset-alias",
                asset.Sha256 is null ? null : asset.Key,
                false,
                false,
                null,
                [asset.Key],
                [],
                new[]
                    {
                        "media-inventory.json",
                        asset.SavedAs ?? "media-inventory.json"
                    }
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                asset.Sha256));
        }

        return expected
            .OrderBy(route => route.LegacyPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<string> BuildRetainedSitemapUrls(
        string root,
        HttpRecord[] httpRecords,
        HashSet<string> reasons)
    {
        var sitemapUrls = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var record in httpRecords
            .Where(record =>
                record.SavedAs is not null
                && record.SavedAs.StartsWith(
                    "raw/sitemaps/",
                    StringComparison.Ordinal))
            .OrderBy(record => record.Url, StringComparer.Ordinal))
        {
            var relative = record.SavedAs!;
            try
            {
                if (record.Status is not (>= 200 and < 300)
                    || record.ContentLength is null
                    || string.IsNullOrWhiteSpace(record.Sha256)
                    || !Uri.TryCreate(record.Url, UriKind.Absolute, out var sitemapUri)
                    || !IsCanonicalPublicUri(sitemapUri))
                {
                    reasons.Add($"sitemap-http-binding-invalid:{relative}");
                    continue;
                }

                var path = ResolveChildPath(root, relative);
                var bytes = File.ReadAllBytes(path);
                if (bytes.LongLength != record.ContentLength
                    || !CaptureIO.Sha256(bytes).Equals(
                        record.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add($"sitemap-http-binding-invalid:{relative}");
                    continue;
                }

                var document = BaselineCaptureService.ParseSitemapDocument(
                    bytes);
                var locations = document.Root?.Name.LocalName == "urlset"
                    ? document.Root.Elements()
                        .Where(element => element.Name.LocalName == "url")
                        .SelectMany(element => element.Elements()
                            .Where(child => child.Name.LocalName == "loc"))
                        .Select(element => element.Value.Trim())
                    : [];
                foreach (var location in locations)
                {
                    if (Uri.TryCreate(
                            location,
                            UriKind.Absolute,
                            out var uri)
                        && IsCanonicalPublicUri(uri))
                    {
                        sitemapUrls.Add(NormalizeEvidenceUrl(uri));
                    }
                    else
                    {
                        reasons.Add($"sitemap-location-invalid:{relative}");
                    }
                }
            }
            catch (Exception ex) when (
                ex is System.Xml.XmlException
                    or InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
            {
                reasons.Add($"sitemap-evidence-invalid:{relative}");
            }
        }

        return sitemapUrls;
    }

    private static string NormalizeEvidenceUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri.Normalize(NormalizationForm.FormC);
    }

    private static bool IsCanonicalPublicUri(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal)
        && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && uri.Host.Equals(
            CaptureProfile.Approved.PrimaryOrigin.Host,
            StringComparison.OrdinalIgnoreCase);

    private static bool RouteEntriesMatch(
        RouteEntry actual,
        RouteEntry expected) =>
        actual.RouteId == expected.RouteId
        && actual.LegacyPath == expected.LegacyPath
        && actual.CanonicalPath == expected.CanonicalPath
        && actual.ExpectedStatus == expected.ExpectedStatus
        && actual.RedirectTarget == expected.RedirectTarget
        && actual.TemplateKey == expected.TemplateKey
        && actual.ContentKey == expected.ContentKey
        && actual.Indexable == expected.Indexable
        && actual.Sitemap == expected.Sitemap
        && actual.MetadataKey == expected.MetadataKey
        && (actual.AssetKeys ?? [])
            .SequenceEqual(expected.AssetKeys, StringComparer.Ordinal)
        && (actual.DynamicRegionKeys ?? [])
            .SequenceEqual(
                expected.DynamicRegionKeys,
                StringComparer.Ordinal)
        && (actual.EvidenceRefs ?? [])
            .SequenceEqual(expected.EvidenceRefs, StringComparer.Ordinal)
        && actual.ContentChecksum == expected.ContentChecksum;

    private static void ValidateImportCandidateSemantics(
        IReadOnlyList<ImportCandidate> candidates,
        CaptureSummary summary,
        HttpRecord[] httpRecords,
        MetadataRecord[] metadata,
        AssetRecord[] assets,
        NavigationItem[] navigation,
        FormsWidgetsDocument forms,
        HashSet<string> reasons)
    {
        var expected = BuildExpectedImportCandidates(
            summary,
            httpRecords,
            metadata,
            assets,
            navigation,
            forms);
        var keyedCandidates = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.SourceKey))
            .ToArray();
        var actualGroups = keyedCandidates
            .GroupBy(candidate => candidate.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var expectedKeys = expected
            .Select(candidate => candidate.SourceKey)
            .ToHashSet(StringComparer.Ordinal);
        var actualKeys = actualGroups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (keyedCandidates.Length != candidates.Count
            || actualGroups.Any(group => group.Count() != 1)
            || !actualKeys.SetEquals(expectedKeys))
        {
            reasons.Add("import-candidate-set-mismatch");
        }

        if (actualGroups.All(group => group.Count() == 1)
            && actualKeys.SetEquals(expectedKeys)
            && !candidates
                .Select(candidate => candidate.SourceKey)
                .SequenceEqual(
                    expected.Select(candidate => candidate.SourceKey),
                    StringComparer.Ordinal))
        {
            reasons.Add("import-candidate-order-mismatch");
        }

        var actualBySourceKey = actualGroups
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.Single(),
                StringComparer.Ordinal);
        foreach (var expectedCandidate in expected)
        {
            if (!actualBySourceKey.TryGetValue(
                    expectedCandidate.SourceKey,
                    out var actual))
            {
                continue;
            }

            if (actual.Kind != expectedCandidate.Kind)
            {
                reasons.Add(
                    $"import-candidate-kind-mismatch:{actual.SourceKey}");
            }

            if (actual.SourceVersion != expectedCandidate.SourceVersion
                || actual.SourceUri != expectedCandidate.SourceUri
                || actual.TargetKey != expectedCandidate.TargetKey
                || actual.CanonicalPath != expectedCandidate.CanonicalPath
                || actual.ExpectedTargetChecksum
                    != expectedCandidate.ExpectedTargetChecksum)
            {
                reasons.Add(
                    $"import-candidate-source-association-invalid:{actual.SourceKey}");
            }

            if (actual.PayloadRef != expectedCandidate.PayloadRef)
            {
                reasons.Add(
                    $"import-candidate-payload-ref-mismatch:{actual.SourceKey}");
            }

            if (actual.SourceChecksum != expectedCandidate.SourceChecksum)
            {
                reasons.Add(
                    $"import-candidate-checksum-mismatch:{actual.SourceKey}");
            }

            if (!(actual.MediaRefs ?? []).SequenceEqual(
                    expectedCandidate.MediaRefs,
                    StringComparer.Ordinal))
            {
                reasons.Add(
                    $"import-candidate-media-refs-mismatch:{actual.SourceKey}");
            }

            if (!(actual.DependencyKeys ?? []).SequenceEqual(
                    expectedCandidate.DependencyKeys,
                    StringComparer.Ordinal))
            {
                reasons.Add(
                    $"import-candidate-dependency-keys-mismatch:{actual.SourceKey}");
            }

            if (actual.Decision != expectedCandidate.Decision
                || actual.ConflictReason != expectedCandidate.ConflictReason)
            {
                reasons.Add(
                    $"import-candidate-decision-mismatch:{actual.SourceKey}");
            }
        }
    }

    private static ImportCandidate[] BuildExpectedImportCandidates(
        CaptureSummary summary,
        HttpRecord[] httpRecords,
        MetadataRecord[] metadata,
        AssetRecord[] assets,
        NavigationItem[] navigation,
        FormsWidgetsDocument forms)
    {
        var candidates = new List<ImportCandidate>();
        var retainedAssets = assets.ToList();
        var assetSourcePages = retainedAssets
            .GroupBy(asset => asset.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(asset => asset.SourcePage)
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        foreach (var record in httpRecords
            .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase))
        {
            var uri = new Uri(record.Url);
            if (uri.Query.Length > 0)
            {
                candidates.Add(CreateExpectedImportCandidate(
                    summary.CaptureId,
                    "endpoint",
                    record.Url,
                    record.Sha256 ?? HashEvidenceRecord(record),
                    record.Url,
                    null,
                    record.SavedAs ?? "http-inventory.json",
                    record.Status is null ? "reject" : "update",
                    record.Status is null
                        ? record.Error ?? "Query endpoint capture failed."
                        : "Contractually significant query endpoint is represented by sourceUri; canonicalPath is omitted to satisfy C5.",
                    [],
                    []));
                continue;
            }

            if (record.Redirects.Count > 0)
            {
                candidates.Add(CreateExpectedImportCandidate(
                    summary.CaptureId,
                    "redirect",
                    record.Url,
                    HashEvidenceRecord(record),
                    record.Url,
                    record.Path.Contains('?', StringComparison.Ordinal)
                        ? null
                        : record.Path,
                    "http-inventory.json",
                    "update",
                    "Legacy redirect must be reconciled to a single permanent target.",
                    [],
                    []));
                continue;
            }

            if (record.Status is not (>= 200 and < 300)
                || record.Sha256 is null
                || record.SavedAs is null)
            {
                candidates.Add(CreateExpectedImportCandidate(
                    summary.CaptureId,
                    "endpoint",
                    record.Url,
                    HashEvidenceRecord(record),
                    record.Url,
                    record.Path.Contains('?', StringComparison.Ordinal)
                        ? null
                        : record.Path,
                    "http-inventory.json",
                    "reject",
                    $"Captured endpoint was not importable: HTTP {record.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} {record.Error}".Trim(),
                    [],
                    []));
                continue;
            }

            var html = record.ContentType?.Contains(
                "html",
                StringComparison.OrdinalIgnoreCase) == true;
            var template = BaselineCaptureService.ClassifyTemplate(
                uri.AbsolutePath);
            var kind = html && template == "religious-content"
                ? "religious-content"
                : html
                    ? "content"
                    : "endpoint";
            candidates.Add(CreateExpectedImportCandidate(
                summary.CaptureId,
                kind,
                record.Url,
                record.Sha256,
                record.Url,
                record.Path.Contains('?', StringComparison.Ordinal)
                    ? null
                    : record.Path,
                record.SavedAs,
                html ? "create" : "update",
                html
                    ? null
                    : "Generated public endpoint must be recreated rather than copied as editable content.",
                BaselineCaptureService.SelectMediaRefsForSourcePage(
                    retainedAssets,
                    assetSourcePages,
                    record.Url),
                []));
        }

        var directContentUrls = httpRecords
            .Where(record =>
                record.Redirects.Count == 0
                && new Uri(record.Url).Query.Length == 0)
            .Select(record => record.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in metadata
            .Where(item => directContentUrls.Contains(item.Url))
            .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase))
        {
            var path = NormalizeImportPath(new Uri(item.Url));
            candidates.Add(CreateExpectedImportCandidate(
                summary.CaptureId,
                "metadata",
                item.Url,
                HashEvidenceRecord(item),
                item.Url,
                path.Contains('?', StringComparison.Ordinal) ? null : path,
                "metadata-inventory.json",
                "update",
                "SEO metadata is reconciled independently from body content.",
                [],
                [
                    ExpectedCandidateSourceKey(
                        BaselineCaptureService.ClassifyTemplate(
                            new Uri(item.Url).AbsolutePath)
                            == "religious-content"
                                ? "religious-content"
                                : "content",
                        item.Url)
                ]));
        }

        foreach (var group in navigation
            .Where(item => directContentUrls.Contains(item.SourcePage))
            .GroupBy(
                item => item.SourcePage,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(CreateExpectedImportCandidate(
                summary.CaptureId,
                "navigation",
                group.Key,
                HashEvidenceRecord(group
                    .OrderBy(item => item.Order)
                    .ToArray()),
                group.Key,
                NormalizeImportPath(new Uri(group.Key)),
                "navigation-inventory.json",
                "update",
                "Hierarchy, ordering, active, focusable, desktop and mobile states require explicit editorial reconciliation.",
                [],
                []));
        }

        foreach (var form in forms.Forms
            .Where(item => directContentUrls.Contains(item.SourcePage))
            .OrderBy(item => item.FormKey, StringComparer.Ordinal))
        {
            candidates.Add(CreateExpectedImportCandidate(
                summary.CaptureId,
                "form",
                form.FormKey,
                HashEvidenceRecord(form),
                form.SourcePage,
                NormalizeImportPath(new Uri(form.SourcePage)),
                "forms-widgets.json",
                "conflict",
                "Public markup is observable, but destination, anti-abuse, consent and success workflow require authorized export/non-production evidence.",
                [],
                []));
        }

        candidates.Add(CreateExpectedImportCandidate(
            summary.CaptureId,
            "editable-setting",
            "site-settings",
            HashEvidenceRecord(new
            {
                BaseUri = new Uri(summary.SourceBaseUrl),
                Navigation = navigation.Length,
                DynamicRegions = forms.DynamicRegions.Count
            }),
            summary.SourceBaseUrl,
            "/",
            "forms-widgets.json",
            "conflict",
            "Provider credentials, payment configuration, form destinations and widget settings are not observable from public GET evidence.",
            [],
            []));

        var contentUrls = httpRecords
            .Where(record =>
                record.ContentType?.Contains(
                    "html",
                    StringComparison.OrdinalIgnoreCase) == true)
            .Select(record => record.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets
            .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase))
        {
            if (contentUrls.Contains(asset.Url))
            {
                continue;
            }

            var decision = asset.Kind == "rejected"
                ? "reject"
                : asset.Ownership
                    is "same-origin-plugin"
                        or "same-origin-theme"
                        or "same-origin-core"
                    ? "skip"
                    : string.IsNullOrWhiteSpace(asset.SourcePage)
                        ? "orphan"
                        : "create";
            var reason = decision switch
            {
                "reject" => asset.Error
                    ?? "Response was not an approved successful media type.",
                "skip" =>
                    "Technical WordPress/plugin/theme implementation asset is evidence only and must not be migrated as editable media.",
                "orphan" =>
                    "Successful media has no captured content reference and requires manual reconciliation.",
                _ => null
            };
            candidates.Add(CreateExpectedImportCandidate(
                summary.CaptureId,
                "media",
                asset.Url,
                asset.Sha256 ?? HashEvidenceRecord(new
                {
                    asset.Url,
                    asset.Status,
                    asset.Error
                }),
                asset.Url,
                null,
                asset.SavedAs ?? "media-inventory.json",
                decision,
                reason,
                [],
                []));
        }

        return candidates
            .GroupBy(
                candidate => candidate.SourceKey,
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.SourceKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static ImportCandidate CreateExpectedImportCandidate(
        string captureId,
        string kind,
        string identity,
        string checksum,
        string? sourceUri,
        string? canonicalPath,
        string payloadRef,
        string decision,
        string? reason,
        IReadOnlyList<string> mediaRefs,
        IReadOnlyList<string> dependencyKeys) =>
        new(
            kind == "media"
                ? BaselineCaptureService.MediaCandidateKey(identity)
                : ExpectedCandidateSourceKey(kind, identity),
            captureId,
            checksum,
            kind,
            sourceUri,
            CaptureIO.StableKey($"target-{kind}", identity),
            canonicalPath,
            payloadRef,
            mediaRefs,
            dependencyKeys,
            null,
            decision,
            reason);

    private static string ExpectedCandidateSourceKey(
        string kind,
        string identity) =>
        CaptureIO.StableKey("source", $"{kind}:{identity}");

    private static string NormalizeImportPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath)
            .Normalize(NormalizationForm.FormC);
        if (!path.StartsWith('/'))
        {
            path = $"/{path}";
        }

        return string.IsNullOrEmpty(uri.Query)
            ? path
            : $"{path}{uri.Query}";
    }

    private static void ValidateImportCandidateMediaRefs(
        IReadOnlyList<ImportCandidate> candidates,
        AssetRecord[] assets,
        HashSet<string> reasons)
    {
        var retainedAssets = assets.ToList();
        var sourcePages = retainedAssets
            .GroupBy(asset => asset.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(asset => asset.SourcePage)
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var expected = candidate.Kind is "content" or "religious-content"
                && candidate.SourceUri is not null
                    ? BaselineCaptureService.SelectMediaRefsForSourcePage(
                        retainedAssets,
                        sourcePages,
                        candidate.SourceUri)
                    : [];
            if (!(candidate.MediaRefs ?? [])
                .SequenceEqual(expected, StringComparer.Ordinal))
            {
                reasons.Add(
                    $"import-candidate-media-refs-mismatch:{candidate.SourceKey}");
            }
        }
    }

    private static string NormalizeManifestPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath)
            .Normalize(NormalizationForm.FormC);
        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static async Task ValidateImportCandidateChecksumsAsync(
        string root,
        CaptureSummary summary,
        IReadOnlyList<ImportCandidate> candidates,
        HttpRecord[] httpRecords,
        MetadataRecord[] metadata,
        AssetRecord[] assets,
        NavigationItem[] navigation,
        FormsWidgetsDocument forms,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? expectedChecksum = null;
            string? expectedPayload = null;
            string? identity = candidate.SourceUri;
            if (candidate.Kind == "form")
            {
                var form = forms.Forms.SingleOrDefault(item =>
                    candidate.SourceKey == CaptureIO.StableKey(
                        "source",
                        $"form:{item.FormKey}"));
                if (form is not null)
                {
                    identity = form.FormKey;
                    expectedPayload = "forms-widgets.json";
                    expectedChecksum = HashEvidenceRecord(form);
                }
            }
            else if (candidate.Kind == "editable-setting")
            {
                identity = "site-settings";
                expectedPayload = "forms-widgets.json";
                expectedChecksum = HashEvidenceRecord(new
                {
                    BaseUri = new Uri(summary.SourceBaseUrl),
                    Navigation = navigation.Length,
                    DynamicRegions = forms.DynamicRegions.Count
                });
            }
            else if (candidate.Kind == "metadata")
            {
                expectedPayload = "metadata-inventory.json";
                expectedChecksum = metadata
                    .SingleOrDefault(record => record.Url == candidate.SourceUri) is { } record
                        ? HashEvidenceRecord(record)
                        : null;
            }
            else if (candidate.Kind == "navigation")
            {
                expectedPayload = "navigation-inventory.json";
                expectedChecksum = candidate.SourceUri is { } sourceUri
                    ? HashEvidenceRecord(navigation
                            .Where(item => item.SourcePage == sourceUri)
                            .OrderBy(item => item.Order)
                            .ToArray())
                    : null;
            }
            else if (candidate.Kind == "media")
            {
                var asset = assets.SingleOrDefault(item => item.Url == candidate.SourceUri);
                if (asset is not null)
                {
                    expectedPayload = asset.SavedAs ?? "media-inventory.json";
                    expectedChecksum = asset.Sha256
                        ?? HashEvidenceRecord(new
                        {
                            asset.Url,
                            asset.Status,
                            asset.Error
                        });
                }
            }
            else
            {
                var record = httpRecords.SingleOrDefault(item =>
                    item.Url == candidate.SourceUri);
                if (record is not null)
                {
                    expectedPayload = candidate.Kind == "redirect"
                        || record.SavedAs is null
                            ? "http-inventory.json"
                            : record.SavedAs;
                    expectedChecksum = expectedPayload == "http-inventory.json"
                        ? HashEvidenceRecord(record)
                        : record.Sha256;
                }
            }

            if (expectedPayload is not null
                && (expectedPayload.StartsWith("raw/", StringComparison.Ordinal)
                    || expectedPayload.StartsWith("assets/", StringComparison.Ordinal)))
            {
                var payload = ResolveChildPath(root, expectedPayload);
                if (!File.Exists(payload)
                    || expectedChecksum is null
                    || await CaptureIO.Sha256FileAsync(payload, cancellationToken)
                        != expectedChecksum)
                {
                    expectedChecksum = null;
                }
            }

            var expectedSourceKey = identity is null
                ? null
                : candidate.Kind == "media"
                    ? BaselineCaptureService.MediaCandidateKey(identity)
                    : CaptureIO.StableKey("source", $"{candidate.Kind}:{identity}");
            var expectedTargetKey = identity is null
                ? null
                : CaptureIO.StableKey($"target-{candidate.Kind}", identity);
            if (expectedSourceKey is null
                || expectedTargetKey is null
                || expectedSourceKey != candidate.SourceKey
                || expectedTargetKey != candidate.TargetKey)
            {
                reasons.Add(
                    $"import-candidate-source-association-invalid:{candidate.SourceKey}");
            }

            if (expectedPayload is null
                || expectedPayload != candidate.PayloadRef)
            {
                reasons.Add(
                    $"import-candidate-payload-ref-mismatch:{candidate.SourceKey}");
            }

            if (expectedChecksum is null
                || !expectedChecksum.Equals(
                    candidate.SourceChecksum,
                    StringComparison.Ordinal))
            {
                reasons.Add($"import-candidate-checksum-mismatch:{candidate.SourceKey}");
            }

            if (candidate.PayloadRef.StartsWith("raw/", StringComparison.Ordinal)
                || candidate.PayloadRef.StartsWith("assets/", StringComparison.Ordinal))
            {
                var candidatePayload = ResolveChildPath(root, candidate.PayloadRef);
                if (!File.Exists(candidatePayload)
                    || await CaptureIO.Sha256FileAsync(
                        candidatePayload,
                        cancellationToken) != candidate.SourceChecksum)
                {
                    reasons.Add(
                        $"import-candidate-checksum-mismatch:{candidate.SourceKey}");
                }
            }
        }
    }

    private static string HashEvidenceRecord<T>(T value) =>
        CaptureIO.Sha256(
            JsonSerializer.SerializeToUtf8Bytes(value, CaptureIO.JsonOptions));

    private static async Task ValidateFrozenManifestSchemasAsync(
        string root,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        using var routeDocument = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(root, "route-manifest.json"),
                cancellationToken));
        var routeRoot = routeDocument.RootElement;
        var routeRootAllowed = new HashSet<string>(
            ["schemaVersion", "captureId", "capturedAtUtc", "sourceBaseUrl", "routes"],
            StringComparer.Ordinal);
        if (!HasOnlyProperties(routeRoot, routeRootAllowed)
            || routeRoot.GetProperty("schemaVersion").GetString() != EvidenceSchemaVersion
            || string.IsNullOrWhiteSpace(routeRoot.GetProperty("captureId").GetString())
            || !DateTimeOffset.TryParse(
                routeRoot.GetProperty("capturedAtUtc").GetString(),
                out _)
            || !Uri.TryCreate(
                routeRoot.GetProperty("sourceBaseUrl").GetString(),
                UriKind.Absolute,
                out var source)
            || source.Scheme != Uri.UriSchemeHttps
            || routeRoot.GetProperty("routes").GetArrayLength() == 0)
        {
            reasons.Add("route-manifest-schema-invalid");
        }

        var routeAllowed = new HashSet<string>(
            [
                "routeId", "legacyPath", "canonicalPath", "expectedStatus", "redirectTarget",
                "templateKey", "contentKey", "indexable", "sitemap", "metadataKey", "assetKeys",
                "dynamicRegionKeys", "evidenceRefs", "contentChecksum"
            ],
            StringComparer.Ordinal);
        var routeRequired = new[]
        {
            "routeId", "legacyPath", "canonicalPath", "expectedStatus", "templateKey",
            "indexable", "sitemap", "assetKeys", "dynamicRegionKeys", "evidenceRefs"
        };
        var canonicalPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routeRoot.GetProperty("routes").EnumerateArray())
        {
            var status = route.TryGetProperty("expectedStatus", out var statusProperty)
                && statusProperty.TryGetInt32(out var parsedStatus)
                    ? parsedStatus
                    : -1;
            var hasRedirect = route.TryGetProperty("redirectTarget", out var redirect);
            if (route.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(route, routeAllowed)
                || !HasRequiredProperties(route, routeRequired)
                || !IsNonEmptyString(route, "routeId")
                || !IsContractPath(route, "legacyPath")
                || !IsContractPath(route, "canonicalPath")
                || !canonicalPaths.Add(route.GetProperty("canonicalPath").GetString()!)
                || status is not (200 or 301 or 308 or 404 or 410)
                || (status is 301 or 308
                    ? !hasRedirect || !IsContractPath(route, "redirectTarget")
                    : hasRedirect)
                || !IsNonEmptyString(route, "templateKey")
                || route.GetProperty("indexable").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || route.GetProperty("sitemap").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !HasUniqueNonEmptyStrings(route, "assetKeys")
                || !HasUniqueNonEmptyStrings(route, "dynamicRegionKeys")
                || !HasUniqueNonEmptyStrings(route, "evidenceRefs")
                || route.TryGetProperty("contentKey", out var contentKey)
                    && (contentKey.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(contentKey.GetString()))
                || route.TryGetProperty("metadataKey", out var metadataKey)
                    && (metadataKey.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(metadataKey.GetString()))
                || route.TryGetProperty("contentChecksum", out var contentChecksum)
                    && !IsSha256(contentChecksum.GetString()))
            {
                reasons.Add("route-manifest-schema-invalid");
                break;
            }
        }

        using var importDocument = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                Path.Combine(root, "migration-import-manifest.json"),
                cancellationToken));
        var importRoot = importDocument.RootElement;
        var importRootAllowed = new HashSet<string>(
            [
                "schemaVersion", "source", "sourceVersion", "capturedAtUtc", "rightsProfile",
                "mode", "planId", "candidates"
            ],
            StringComparer.Ordinal);
        var mode = importRoot.TryGetProperty("mode", out var modeProperty)
            ? modeProperty.GetString()
            : "dry-run";
        var hasPlanId = importRoot.TryGetProperty("planId", out var planId);
        if (!HasOnlyProperties(importRoot, importRootAllowed)
            || importRoot.GetProperty("schemaVersion").GetString() != EvidenceSchemaVersion
            || !IsNonEmptyString(importRoot, "source")
            || !IsNonEmptyString(importRoot, "sourceVersion")
            || !DateTimeOffset.TryParse(importRoot.GetProperty("capturedAtUtc").GetString(), out _)
            || !IsNonEmptyString(importRoot, "rightsProfile")
            || mode is not ("dry-run" or "apply")
            || (mode == "apply") != hasPlanId
            || hasPlanId && !Guid.TryParseExact(planId.GetString(), "D", out _))
        {
            reasons.Add("import-manifest-schema-invalid");
        }

        var candidateAllowed = new HashSet<string>(
            [
                "sourceKey", "sourceVersion", "sourceChecksum", "kind", "sourceUri",
                "targetKey", "canonicalPath", "payloadRef", "mediaRefs", "dependencyKeys",
                "expectedTargetChecksum", "decision", "conflictReason"
            ],
            StringComparer.Ordinal);
        var candidateRequired = new[]
        {
            "sourceKey", "sourceVersion", "sourceChecksum", "kind", "targetKey",
            "payloadRef", "mediaRefs", "dependencyKeys", "decision"
        };
        foreach (var candidate in importRoot.GetProperty("candidates").EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object
                || !HasOnlyProperties(candidate, candidateAllowed)
                || !HasRequiredProperties(candidate, candidateRequired)
                || !IsNonEmptyString(candidate, "sourceKey")
                || !IsNonEmptyString(candidate, "sourceVersion")
                || !IsSha256(candidate.GetProperty("sourceChecksum").GetString())
                || !IsNonEmptyString(candidate, "kind")
                || candidate.GetProperty("kind").GetString()
                    is not ("content"
                        or "religious-content"
                        or "endpoint"
                        or "redirect"
                        or "metadata"
                        or "navigation"
                        or "form"
                        or "editable-setting"
                        or "media")
                || !IsNonEmptyString(candidate, "targetKey")
                || !IsNonEmptyString(candidate, "payloadRef")
                || !HasUniqueNonEmptyStrings(candidate, "mediaRefs")
                || !HasUniqueNonEmptyStrings(candidate, "dependencyKeys")
                || candidate.GetProperty("decision").GetString()
                    is not ("create" or "update" or "skip" or "conflict" or "orphan" or "reject")
                || candidate.TryGetProperty("sourceUri", out var sourceUri)
                    && (sourceUri.ValueKind != JsonValueKind.String
                        || !Uri.TryCreate(sourceUri.GetString(), UriKind.Absolute, out var parsedSourceUri)
                        || !string.IsNullOrEmpty(parsedSourceUri.UserInfo)
                        || EvidenceSanitizer.HasSensitiveQueryParameter(parsedSourceUri))
                || candidate.TryGetProperty("canonicalPath", out _)
                    && !IsContractPath(candidate, "canonicalPath")
                || candidate.TryGetProperty("expectedTargetChecksum", out var expectedChecksum)
                    && !IsSha256(expectedChecksum.GetString())
                || candidate.TryGetProperty("conflictReason", out var conflictReason)
                    && (conflictReason.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(conflictReason.GetString())))
            {
                reasons.Add("import-manifest-schema-invalid");
                break;
            }
        }
    }

    private static bool HasOnlyProperties(
        JsonElement element,
        HashSet<string> allowed) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().All(property => allowed.Contains(property.Name));

    private static bool HasRequiredProperties(
        JsonElement element,
        IEnumerable<string> required) =>
        required.All(name => element.TryGetProperty(name, out _));

    private static bool IsNonEmptyString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString());

    private static bool IsContractPath(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } path)
        {
            return false;
        }

        return IsContractPath(path);
    }

    private static bool IsContractPath(string path) =>
        path.Length > 0
        && path[0] == '/'
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('?')
        && !path.Contains('#')
        && path == path.Normalize(NormalizationForm.FormC);

    private static bool HasUniqueNonEmptyStrings(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = property.EnumerateArray().ToArray();
        return values.All(value =>
                value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            && values.Select(value => value.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .Count() == values.Length;
    }

    private static async Task ValidateDeterminismAndReviewAsync(
        string root,
        ScreenshotRecord[] screenshots,
        ScreenshotDeterminismEvidence determinism,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        var metricDifferences = determinism.MetricDifferences ?? [];
        var pixelDifferences = determinism.PixelDifferences ?? [];
        var screenshotHashes = determinism.ScreenshotHashes ?? [];
        var hashGroups = screenshotHashes
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var screenshotByKey = screenshots
            .GroupBy(Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        if (hashGroups.Any(group => group.Count() != 1)
            || !hashGroups.Select(group => group.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(CaptureProfile.Approved.ExpectedScreenshotKeys))
        {
            reasons.Add("determinism-screenshot-hash-set-invalid");
        }

        foreach (var item in screenshotHashes)
        {
            if (!screenshotByKey.TryGetValue(item.Key, out var screenshot)
                || !IsSha256(item.RunASha256)
                || !IsSha256(item.RunBSha256)
                || item.RunASha256 != screenshot.Sha256)
            {
                reasons.Add($"determinism-run-a-hash-mismatch:{item.Key}");
            }
        }

        var pixelDifferenceGroups = pixelDifferences
            .GroupBy(difference => difference.Key, StringComparer.Ordinal)
            .ToArray();
        if (pixelDifferenceGroups.Any(group => group.Count() != 1)
            || pixelDifferenceGroups.Any(group =>
                !CaptureProfile.Approved.ExpectedScreenshotKeys.Contains(group.Key)))
        {
            reasons.Add("pixel-difference-key-set-invalid");
        }

        var expectedDiffFiles = pixelDifferences
            .Select(difference => difference.UnmaskedDiffPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.Ordinal);
        if (expectedDiffFiles.Count != pixelDifferences.Count
            || expectedDiffFiles.Any(path =>
                !path.StartsWith("screenshot-diffs/", StringComparison.Ordinal)
                || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add("visual-diff-path-set-invalid");
        }

        var actualDiffFiles = EnumeratePathsWithoutTraversal(root, reasons)
            .Select(path => NormalizeRelative(root, path))
            .Where(path => path.StartsWith("screenshot-diffs/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        if (!actualDiffFiles.SetEquals(expectedDiffFiles))
        {
            reasons.Add("visual-diff-file-set-mismatch");
        }

        if (!determinism.Passed
            || determinism.RunAScreenshotCount != 36
            || determinism.RunBScreenshotCount != 36
            || determinism.MetricDifferenceCount != 0
            || metricDifferences.Count != 0)
        {
            reasons.Add("screenshot-determinism-failed");
        }

        if (determinism.PixelHashDifferenceCount != pixelDifferences.Count)
        {
            reasons.Add("pixel-difference-count-mismatch");
        }

        var hashesByKey = hashGroups
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        foreach (var difference in pixelDifferences)
        {
            if (!hashesByKey.TryGetValue(difference.Key, out var hashes)
                || hashes.RunASha256 != difference.RunASha256
                || hashes.RunBSha256 != difference.RunBSha256)
            {
                reasons.Add($"pixel-difference-hash-mismatch:{difference.Key}");
            }
        }

        if (pixelDifferences.Count == 0)
        {
            if (determinism.ReviewStatus != "not-required")
            {
                reasons.Add("visual-review-status-invalid");
            }

            if (File.Exists(Path.Combine(root, "screenshot-visual-review.json")))
            {
                reasons.Add("visual-review-unexpected");
            }

            return;
        }

        if (determinism.ReviewStatus != "accepted")
        {
            reasons.Add("visual-review-not-accepted");
            return;
        }

        var reviewPath = Path.Combine(root, "screenshot-visual-review.json");
        if (!File.Exists(reviewPath))
        {
            reasons.Add("visual-review-missing");
            return;
        }

        ScreenshotVisualReviewEntry[] reviews;
        try
        {
            reviews = await ScreenshotVisualReviewParser.ParseAsync(
                reviewPath,
                cancellationToken);
        }
        catch (JsonException)
        {
            reasons.Add("visual-review-json-invalid");
            return;
        }

        var screenshotsByKey = screenshots.ToDictionary(Key, StringComparer.Ordinal);
        var byKey = reviews.GroupBy(item => item.Key, StringComparer.Ordinal).ToArray();
        if (byKey.Any(group => group.Count() != 1)
            || !byKey.Select(group => group.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(pixelDifferenceGroups.Select(group => group.Key)))
        {
            reasons.Add("visual-review-key-set-invalid");
        }

        foreach (var difference in pixelDifferences)
        {
            var review = byKey.SingleOrDefault(group => group.Key == difference.Key)?.FirstOrDefault();
            if (review is null
                || review.RunASha256 != difference.RunASha256
                || review.RunBSha256 != difference.RunBSha256
                || review.UnmaskedDiffPath != difference.UnmaskedDiffPath
                || review.Decision != "accept"
                || string.IsNullOrWhiteSpace(review.Reviewer)
                || string.IsNullOrWhiteSpace(review.Rationale)
                || !ScreenshotDeterminismComparer.IsSha256(review.RunASha256)
                || !ScreenshotDeterminismComparer.IsSha256(review.RunBSha256)
                || !ScreenshotDeterminismComparer.IsValidReviewedUtc(
                    review.ReviewedUtc,
                    determinism.RunBCompletedAtUtc,
                    DateTimeOffset.UtcNow)
                || !File.Exists(ResolveChildPath(root, difference.UnmaskedDiffPath)))
            {
                reasons.Add($"visual-review-invalid:{difference.Key}");
                continue;
            }

            if (!screenshotsByKey.TryGetValue(difference.Key, out var screenshot)
                || !ScreenshotVisualReviewParser.IsMaskInBounds(
                    review,
                    screenshot.PngWidth,
                    screenshot.PngHeight))
            {
                reasons.Add($"visual-review-mask-bounds-invalid:{difference.Key}");
                continue;
            }

            try
            {
                var diffPath = ResolveChildPath(root, difference.UnmaskedDiffPath);
                if (new FileInfo(diffPath).Length > PngArtifactInspector.MaximumPngBytes)
                {
                    reasons.Add($"visual-diff-png-too-large:{difference.Key}");
                    continue;
                }

                _ = PngArtifactInspector.Inspect(
                    await File.ReadAllBytesAsync(diffPath, cancellationToken));
            }
            catch (InvalidDataException)
            {
                reasons.Add($"visual-diff-png-invalid:{difference.Key}");
            }
        }
    }

    private static async Task ValidateSealAsync(
        string root,
        IReadOnlyList<BaselineFileManifestEntry> files,
        CaptureSummary summary,
        ScreenshotRecord[] screenshots,
        ScreenshotDeterminismEvidence determinism,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        var verificationPath = Path.Combine(root, "baseline-verification.json");
        var checksumPath = Path.Combine(root, "checksums.sha256");
        if (!File.Exists(verificationPath))
        {
            reasons.Add("baseline-verification-missing");
        }

        if (!File.Exists(checksumPath))
        {
            reasons.Add("checksums-missing");
        }

        if (reasons.Contains("baseline-verification-missing")
            || reasons.Contains("checksums-missing"))
        {
            return;
        }

        var verification = await ReadJsonAsync<BaselineVerificationDocument>(
            verificationPath,
            cancellationToken);
        if (verification.SchemaVersion != EvidenceSchemaVersion
            || !verification.Passed
            || verification.CaptureId != summary.CaptureId
            || verification.VerifiedInputCompletedAtUtc != summary.CompletedAtUtc
            || verification.ScreenshotCount != screenshots.Length
            || verification.CapturedScreenshotCount != screenshots.Count(item => item.Status == "captured")
            || verification.QualityPassScreenshotCount != screenshots.Count(item => item.QualityStatus == "pass")
            || verification.RunBCaptureId != determinism.RunBCaptureId
            || verification.PixelHashDifferenceCount != determinism.PixelHashDifferenceCount
            || verification.ReviewStatus != determinism.ReviewStatus)
        {
            reasons.Add("baseline-verification-content-mismatch");
        }

        var verificationFiles = verification.Files ?? [];
        var validVerificationFiles = verificationFiles
            .Where(file => !string.IsNullOrWhiteSpace(file.RelativePath))
            .ToArray();
        var verificationGroups = validVerificationFiles
            .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (verificationFiles.Count == 0
            || validVerificationFiles.Length != verificationFiles.Count
            || verificationGroups.Any(group => group.Count() != 1))
        {
            reasons.Add("baseline-verification-file-list-invalid");
        }

        var expectedVerificationFiles = files
            .Where(file => file.RelativePath is not ("checksums.sha256" or "baseline-verification.json"))
            .ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var actualVerificationPaths = verificationGroups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (!actualVerificationPaths.SetEquals(expectedVerificationFiles.Keys))
        {
            reasons.Add("baseline-verification-file-coverage-mismatch");
        }

        foreach (var file in verificationFiles)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath))
            {
                reasons.Add("baseline-verification-file-mismatch:[empty]");
                continue;
            }

            if (!expectedVerificationFiles.TryGetValue(file.RelativePath, out var actual)
                || file.Length <= 0
                || file.Length != actual.Length
                || !Regex.IsMatch(file.Sha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || file.Sha256 != actual.Sha256)
            {
                reasons.Add($"baseline-verification-file-mismatch:{file.RelativePath}");
            }
        }

        await ValidateChecksumManifestAndReadmeAsync(
            root,
            checksumPath,
            files,
            summary,
            screenshots,
            determinism,
            reasons,
            cancellationToken);
    }

    private static async Task ValidateRequiredArtifactShapesAsync(
            string root,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            await ValidateObjectShapeAsync(
                root,
                "capture-summary.json",
                [
                    ("captureId", JsonValueKind.String),
                    ("startedAtUtc", JsonValueKind.String),
                    ("completedAtUtc", JsonValueKind.String),
                    ("sourceBaseUrl", JsonValueKind.String),
                    ("noSubmit", JsonValueKind.True),
                    ("sitemapCount", JsonValueKind.Number),
                    ("routeCount", JsonValueKind.Number),
                    ("successfulRouteCount", JsonValueKind.Number),
                    ("redirectCount", JsonValueKind.Number),
                    ("metadataCount", JsonValueKind.Number),
                    ("assetCount", JsonValueKind.Number),
                    ("downloadedAssetCount", JsonValueKind.Number),
                    ("formCount", JsonValueKind.Number),
                    ("dynamicRegionCount", JsonValueKind.Number),
                    ("screenshotCount", JsonValueKind.Number),
                    ("successfulScreenshotCount", JsonValueKind.Number),
                    ("qualityPassScreenshotCount", JsonValueKind.Number),
                    ("residualRiskCount", JsonValueKind.Number),
                    ("durationSeconds", JsonValueKind.Number),
                    ("networkRequestCount", JsonValueKind.Number),
                    ("cacheHitCount", JsonValueKind.Number)
                ],
                reasons,
                cancellationToken);
            await ValidateObjectShapeAsync(
                root,
                "route-manifest.json",
                [
                    ("schemaVersion", JsonValueKind.String),
                    ("captureId", JsonValueKind.String),
                    ("capturedAtUtc", JsonValueKind.String),
                    ("sourceBaseUrl", JsonValueKind.String),
                    ("routes", JsonValueKind.Array)
                ],
                reasons,
                cancellationToken);
            await ValidateObjectShapeAsync(
                root,
                "migration-import-manifest.json",
                [
                    ("schemaVersion", JsonValueKind.String),
                    ("source", JsonValueKind.String),
                    ("sourceVersion", JsonValueKind.String),
                    ("capturedAtUtc", JsonValueKind.String),
                    ("rightsProfile", JsonValueKind.String),
                    ("candidates", JsonValueKind.Array)
                ],
                reasons,
                cancellationToken);
            foreach (var file in new[]
                     {
                         "http-inventory.json",
                         "metadata-inventory.json",
                         "media-inventory.json",
                         "navigation-inventory.json",
                         "religious-content.json",
                         "screenshots.json",
                         "screenshot-network-decisions.json",
                         "residual-risks.json"
                     })
            {
                await ValidateArrayShapeAsync(root, file, reasons, cancellationToken);
            }

            await ValidateObjectShapeAsync(
                root,
                "forms-widgets.json",
                [
                    ("noSubmit", JsonValueKind.True),
                    ("forms", JsonValueKind.Array),
                    ("dynamicRegions", JsonValueKind.Array)
                ],
                reasons,
                cancellationToken);
            await ValidateObjectShapeAsync(
                root,
                "screenshot-network-policy.json",
                [
                    ("policyVersion", JsonValueKind.String),
                    ("allowedMethods", JsonValueKind.Array),
                    ("allowedOrigins", JsonValueKind.Array),
                    ("allowedStaticHosts", JsonValueKind.Array),
                    ("allowedStaticResourceTypes", JsonValueKind.Array),
                    ("blockedCapabilities", JsonValueKind.Array),
                    ("pinnedDnsAnswers", JsonValueKind.Object),
                    ("chromiumHostResolverRules", JsonValueKind.Array),
                    ("navigationAttempts", JsonValueKind.Number),
                    ("navigationAttemptTimeoutSeconds", JsonValueKind.Number),
                    ("readinessTimeoutSeconds", JsonValueKind.Number),
                    ("captureTimeoutSeconds", JsonValueKind.Number),
                    ("interCaptureDelayMilliseconds", JsonValueKind.Number),
                    ("enforcement", JsonValueKind.String)
                ],
                reasons,
                cancellationToken);
            await ValidateObjectShapeAsync(
                root,
                "screenshot-capture-provenance.json",
                [
                    ("captureId", JsonValueKind.String),
                    ("capturedAtUtc", JsonValueKind.String),
                    ("operatingSystem", JsonValueKind.String),
                    ("architecture", JsonValueKind.String),
                    ("dotNetSdkVersion", JsonValueKind.String),
                    ("playwrightPackageVersion", JsonValueKind.String),
                    ("chromiumVersion", JsonValueKind.String),
                    ("chromiumExecutable", JsonValueKind.String),
                    ("browserInstallCommand", JsonValueKind.String),
                    ("browserCacheKey", JsonValueKind.String),
                    ("networkPolicyVersion", JsonValueKind.String),
                    ("navigationAttempts", JsonValueKind.Number),
                    ("navigationAttemptTimeoutSeconds", JsonValueKind.Number),
                    ("readinessTimeoutSeconds", JsonValueKind.Number),
                    ("captureTimeoutSeconds", JsonValueKind.Number),
                    ("interCaptureDelayMilliseconds", JsonValueKind.Number),
                    ("toolingOnlyDependency", JsonValueKind.True),
                    ("chromiumRevision", JsonValueKind.String),
                    ("expectedExecutableSha256", JsonValueKind.String),
                    ("actualExecutableSha256", JsonValueKind.String),
                    ("chromiumSandbox", JsonValueKind.True),
                    ("childEnvironmentKeys", JsonValueKind.Array),
                    ("browserWorkingDirectory", JsonValueKind.String),
                    ("processElevated", JsonValueKind.False),
                    ("primaryOrigin", JsonValueKind.String),
                    ("pinnedDnsAnswers", JsonValueKind.Object),
                    ("chromiumHostResolverRules", JsonValueKind.Array),
                    ("cookiesUsed", JsonValueKind.False),
                    ("storageStateUsed", JsonValueKind.False),
                    ("permissionsGranted", JsonValueKind.False),
                    ("proxyUsed", JsonValueKind.False),
                    ("credentialsUsed", JsonValueKind.False)
                ],
                reasons,
                cancellationToken);
            await ValidateObjectShapeAsync(
                root,
                "screenshot-determinism.json",
                [
                    ("comparedAtUtc", JsonValueKind.String),
                    ("runACaptureId", JsonValueKind.String),
                    ("runBCaptureId", JsonValueKind.String),
                    ("runAStartedAtUtc", JsonValueKind.String),
                    ("runACapturedAtUtc", JsonValueKind.String),
                    ("runACompletedAtUtc", JsonValueKind.String),
                    ("runBStartedAtUtc", JsonValueKind.String),
                    ("runBCapturedAtUtc", JsonValueKind.String),
                    ("runBCompletedAtUtc", JsonValueKind.String),
                    ("runAScreenshotCount", JsonValueKind.Number),
                    ("runBScreenshotCount", JsonValueKind.Number),
                    ("metricDifferenceCount", JsonValueKind.Number),
                    ("pixelHashDifferenceCount", JsonValueKind.Number),
                    ("comparedMetrics", JsonValueKind.Array),
                    ("metricDifferences", JsonValueKind.Array),
                    ("pixelDifferences", JsonValueKind.Array),
                    ("reviewStatus", JsonValueKind.String),
                    ("passed", JsonValueKind.True),
                    ("screenshotHashes", JsonValueKind.Array)
                ],
                reasons,
                cancellationToken);
        }

        private static async Task ValidateObjectShapeAsync(
            string root,
            string relative,
            IReadOnlyList<(string Name, JsonValueKind Kind)> required,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(Path.Combine(root, relative), cancellationToken));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    reasons.Add($"artifact-root-invalid:{relative}");
                    return;
                }

                foreach (var property in required)
                {
                    if (!document.RootElement.TryGetProperty(property.Name, out var value)
                        || value.ValueKind != property.Kind)
                    {
                        reasons.Add($"artifact-root-field-invalid:{relative}:{property.Name}");
                    }
                }
            }
            catch (JsonException)
            {
                reasons.Add($"artifact-json-invalid:{relative}");
            }
        }

        private static async Task ValidateArrayShapeAsync(
            string root,
            string relative,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            try
            {
                using var document = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(Path.Combine(root, relative), cancellationToken));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    reasons.Add($"artifact-root-invalid:{relative}");
                }
            }
            catch (JsonException)
            {
                reasons.Add($"artifact-json-invalid:{relative}");
            }
        }

        private static async Task ValidateReferencedFileGraphAsync(
            string root,
            IReadOnlyList<BaselineFileManifestEntry> files,
            CaptureSummary summary,
            HttpRecord[] httpRecords,
            AssetRecord[] assets,
            RouteManifest routeManifest,
            ImportManifest importManifest,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            var actualByPath = files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            foreach (var record in httpRecords)
            {
                if (string.IsNullOrWhiteSpace(record.Url)
                    || string.IsNullOrWhiteSpace(record.Path)
                    || record.Redirects is null
                    || record.Headers is null)
                {
                    reasons.Add($"http-inventory-entry-invalid:{record.Url}");
                }

                if (record.SavedAs is null)
                {
                    if (record.Sha256 is not null || record.ContentLength is not null)
                    {
                        reasons.Add($"http-inventory-file-fields-invalid:{record.Url}");
                    }

                    continue;
                }

                await ValidateInventoryFileAsync(
                    root,
                    actualByPath,
                    record.SavedAs,
                    record.ContentLength,
                    record.Sha256,
                    "http",
                    expected,
                    reasons,
                    cancellationToken);

                if (Uri.TryCreate(record.Url, UriKind.Absolute, out var uri)
                    && uri.AbsolutePath.EndsWith("sitemap.xml", StringComparison.OrdinalIgnoreCase))
                {
                    var duplicate = $"raw/sitemaps/{CaptureIO.SafeFileName(uri.AbsolutePath)}.xml";
                    expected.Add(duplicate);
                    await ValidateDuplicateFileAsync(
                        root,
                        record.SavedAs,
                        duplicate,
                        reasons,
                        cancellationToken);
                }

                if (Uri.TryCreate(record.Url, UriKind.Absolute, out uri)
                    && uri.AbsolutePath.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase))
                {
                    const string duplicate = "raw/endpoints/robots.txt";
                    expected.Add(duplicate);
                    await ValidateDuplicateFileAsync(
                        root,
                        record.SavedAs,
                        duplicate,
                        reasons,
                        cancellationToken);
                }
            }

            foreach (var asset in assets)
            {
                if (string.IsNullOrWhiteSpace(asset.Key)
                    || string.IsNullOrWhiteSpace(asset.Url)
                    || string.IsNullOrWhiteSpace(asset.SourcePage)
                    || string.IsNullOrWhiteSpace(asset.Kind)
                    || string.IsNullOrWhiteSpace(asset.Relation)
                    || string.IsNullOrWhiteSpace(asset.Ownership))
                {
                    reasons.Add($"media-inventory-entry-invalid:{asset.Key}");
                }

                if (asset.SavedAs is null)
                {
                    if (asset.Sha256 is not null)
                    {
                        reasons.Add($"media-inventory-file-fields-invalid:{asset.Key}");
                    }

                    continue;
                }

                await ValidateInventoryFileAsync(
                    root,
                    actualByPath,
                    asset.SavedAs,
                    asset.Bytes,
                    asset.Sha256,
                    "asset",
                    expected,
                    reasons,
                    cancellationToken);
            }

            foreach (var reference in (routeManifest.Routes ?? [])
                         .SelectMany(route => route.EvidenceRefs ?? [])
                         .Concat((importManifest.Candidates ?? []).Select(item => item.PayloadRef))
                         .Where(path => path is not null
                             && (path.StartsWith("raw/", StringComparison.Ordinal)
                                 || path.StartsWith("assets/", StringComparison.Ordinal))))
            {
                expected.Add(reference!);
            }

            var actual = files
                .Select(file => file.RelativePath)
                .Where(path => path.StartsWith("raw/", StringComparison.Ordinal)
                    || path.StartsWith("assets/", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            if (!actual.SetEquals(expected))
            {
                foreach (var extra in actual.Except(expected, StringComparer.Ordinal))
                {
                    reasons.Add($"unreferenced-evidence-file:{extra}");
                }

                foreach (var missing in expected.Except(actual, StringComparer.Ordinal))
                {
                    reasons.Add($"referenced-evidence-file-missing:{missing}");
                }
            }

            var sitemapFiles = actual.Count(path =>
                path.StartsWith("raw/sitemaps/", StringComparison.Ordinal)
                && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (summary.SitemapCount != sitemapFiles)
            {
                reasons.Add("sitemap-summary-count-mismatch");
            }
        }

        private static async Task ValidateInventoryFileAsync(
            string root,
            Dictionary<string, BaselineFileManifestEntry> actualByPath,
            string relative,
            long? expectedLength,
            string? expectedSha256,
            string inventoryName,
            HashSet<string> expectedPaths,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            var normalized = relative.Replace('\\', '/');
            if (!(normalized.StartsWith("raw/", StringComparison.Ordinal)
                  || normalized.StartsWith("assets/", StringComparison.Ordinal))
                || !expectedPaths.Add(normalized)
                || !actualByPath.TryGetValue(normalized, out var actual)
                || expectedLength is null
                || expectedLength < 0
                || actual.Length != expectedLength
                || !IsSha256(expectedSha256)
                || actual.Sha256 != expectedSha256
                || await CaptureIO.Sha256FileAsync(
                    ResolveChildPath(root, normalized),
                    cancellationToken) != expectedSha256)
            {
                reasons.Add($"{inventoryName}-inventory-file-mismatch:{normalized}");
            }
        }

        private static async Task ValidateDuplicateFileAsync(
            string root,
            string sourceRelative,
            string duplicateRelative,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            try
            {
                var source = ResolveChildPath(root, sourceRelative);
                var duplicate = ResolveChildPath(root, duplicateRelative);
                if (!File.Exists(source)
                    || !File.Exists(duplicate)
                    || new FileInfo(source).Length != new FileInfo(duplicate).Length
                    || await CaptureIO.Sha256FileAsync(source, cancellationToken)
                        != await CaptureIO.Sha256FileAsync(duplicate, cancellationToken))
                {
                    reasons.Add($"derived-raw-file-mismatch:{duplicateRelative}");
                }
            }
            catch (InvalidDataException)
            {
                reasons.Add($"derived-raw-file-mismatch:{duplicateRelative}");
            }
        }

        private static async Task ValidateAllPngsAsync(
            string root,
            IReadOnlyList<BaselineFileManifestEntry> files,
            HashSet<string> reasons,
            CancellationToken cancellationToken)
        {
            var pngs = files
                .Where(file => file.RelativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (pngs.Length > MaximumEvidencePngCount)
            {
                reasons.Add("evidence-png-count-exceeded");
                return;
            }

            if (!EnsureEvidencePngAggregateWithinLimit(pngs.Select(file => file.Length), out _))
            {
                reasons.Add("evidence-png-aggregate-too-large");
                return;
            }

            foreach (var png in pngs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (png.Length > PngArtifactInspector.MaximumPngBytes)
                {
                    reasons.Add($"evidence-png-too-large:{png.RelativePath}");
                    continue;
                }

                try
                {
                    var inspection = PngArtifactInspector.Inspect(
                        await File.ReadAllBytesAsync(
                            ResolveChildPath(root, png.RelativePath),
                            cancellationToken));
                    if (inspection.Length != png.Length || inspection.Sha256 != png.Sha256)
                    {
                        reasons.Add($"evidence-png-hash-mismatch:{png.RelativePath}");
                    }
                }
                catch (InvalidDataException)
                {
                    reasons.Add($"evidence-png-invalid:{png.RelativePath}");
                }
            }
        }

        internal static bool EnsureEvidencePngAggregateWithinLimit(
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

                if (length > MaximumEvidencePngBytes - total)
                {
                    total = MaximumEvidencePngBytes + 1;
                    return false;
                }

                total += length;
            }

            return true;
        }

    private static async Task ValidateChecksumManifestAndReadmeAsync(
        string root,
        string checksumPath,
        IReadOnlyList<BaselineFileManifestEntry> files,
        CaptureSummary summary,
        ScreenshotRecord[] screenshots,
        ScreenshotDeterminismEvidence determinism,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        var checksumLines = await File.ReadAllLinesAsync(checksumPath, cancellationToken);
        var checksumEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in checksumLines)
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2
                || !Regex.IsMatch(parts[0], "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || !checksumEntries.TryAdd(parts[1], parts[0]))
            {
                reasons.Add("checksum-manifest-invalid");
                continue;
            }

            try
            {
                var path = ResolveChildPath(root, parts[1]);
                if (!File.Exists(path)
                    || await CaptureIO.Sha256FileAsync(path, cancellationToken) != parts[0])
                {
                    reasons.Add($"checksum-mismatch:{parts[1]}");
                }
            }
            catch (InvalidDataException)
            {
                reasons.Add($"checksum-path-invalid:{parts[1]}");
            }
        }

        var expectedFiles = files
            .Select(file => file.RelativePath)
            .Where(relative => relative != "checksums.sha256")
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedFiles.SetEquals(checksumEntries.Keys))
        {
            reasons.Add("checksum-coverage-mismatch");
        }

        var readme = await File.ReadAllTextAsync(Path.Combine(root, "README.md"), cancellationToken);
        var expectedGate = BuildGateSection(summary, screenshots, determinism);
        if (!readme.Contains(expectedGate, StringComparison.Ordinal))
        {
            reasons.Add("readme-gate-summary-mismatch");
        }
    }

    private static async Task ValidateComparisonChecksumManifestAsync(
        string root,
        IReadOnlyList<BaselineFileManifestEntry> files,
        HashSet<string> reasons,
        CancellationToken cancellationToken)
    {
        var checksumPath = Path.Combine(root, "checksums.sha256");
        var checksumLines = await File.ReadAllLinesAsync(
            checksumPath,
            cancellationToken);
        var checksumEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in checksumLines)
        {
            var parts = line.Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2
                || !Regex.IsMatch(
                    parts[0],
                    "^[0-9a-f]{64}$",
                    RegexOptions.CultureInvariant)
                || !checksumEntries.TryAdd(parts[1], parts[0]))
            {
                reasons.Add("comparison-checksum-manifest-invalid");
            }
        }

        var expectedFiles = files
            .Select(file => file.RelativePath)
            .Where(relative => relative != "checksums.sha256")
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedFiles.SetEquals(checksumEntries.Keys))
        {
            reasons.Add("comparison-checksum-coverage-mismatch");
        }

        foreach (var expected in expectedFiles)
        {
            if (!checksumEntries.TryGetValue(expected, out var expectedHash))
            {
                continue;
            }

            try
            {
                var path = ResolveChildPath(root, expected);
                if (!File.Exists(path)
                    || await CaptureIO.Sha256FileAsync(path, cancellationToken)
                        != expectedHash)
                {
                    reasons.Add($"comparison-checksum-mismatch:{expected}");
                }
            }
            catch (InvalidDataException)
            {
                reasons.Add($"comparison-checksum-path-invalid:{expected}");
            }
        }
    }

    private static IReadOnlyList<string> EnumeratePathsWithoutTraversal(
        string root,
        HashSet<string> reasons)
    {
        var files = new List<string>();
        long totalBytes = 0;
        var pending = new Stack<DirectoryInfo>();
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            reasons.Add("reparse-point-root");
            return files;
        }

        pending.Push(rootInfo);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reasons.Add($"reparse-point:{NormalizeRelative(root, entry.FullName)}");
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file)
                {
                    if (files.Count >= MaximumEvidenceFileCount)
                    {
                        reasons.Add("evidence-tree-file-count-exceeded");
                        return files.Order(StringComparer.Ordinal).ToArray();
                    }

                    if (file.Length > MaximumEvidenceFileBytes)
                    {
                        reasons.Add($"evidence-tree-file-too-large:{NormalizeRelative(root, file.FullName)}");
                        return files.Order(StringComparer.Ordinal).ToArray();
                    }

                    if (file.Length > MaximumEvidenceTreeBytes - totalBytes)
                    {
                        reasons.Add("evidence-tree-byte-limit-exceeded");
                        return files.Order(StringComparer.Ordinal).ToArray();
                    }

                    files.Add(file.FullName);
                    totalBytes += file.Length;
                }
            }
        }

        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsAllowedEvidencePath(string relative)
    {
        if (RequiredBaseArtifacts.Contains(relative, StringComparer.Ordinal)
            || relative is "checksums.sha256"
                or "baseline-verification.json"
                or "screenshot-visual-review.json")
        {
            return true;
        }

        return relative.StartsWith("raw/", StringComparison.Ordinal)
            || relative.StartsWith("assets/", StringComparison.Ordinal)
            || relative.StartsWith("screenshots/", StringComparison.Ordinal)
                && relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("screenshot-diffs/", StringComparison.Ordinal)
                && relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedScreenshotComparisonPath(string relative) =>
        RequiredScreenshotComparisonArtifacts.Contains(
            relative,
            StringComparer.Ordinal)
        || relative == "README.md"
        || relative.StartsWith("screenshots/", StringComparison.Ordinal)
            && relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static string UpdateGateSection(
        string readme,
        CaptureSummary summary,
        ScreenshotRecord[] screenshots,
        ScreenshotDeterminismEvidence determinism)
    {
        var gate = BuildGateSection(summary, screenshots, determinism);
        var start = readme.IndexOf(GateStart, StringComparison.Ordinal);
        var end = readme.IndexOf(GateEnd, StringComparison.Ordinal);
        if (start >= 0 && end > start)
        {
            return string.Concat(
                readme.AsSpan(0, start),
                gate,
                readme.AsSpan(end + GateEnd.Length));
        }

        return $"{readme.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{gate}{Environment.NewLine}";
    }

    private static string BuildGateSection(
        CaptureSummary summary,
        ScreenshotRecord[] screenshots,
        ScreenshotDeterminismEvidence determinism)
    {
        var captured = screenshots.Count(item => item.Status == "captured");
        var qualityPass = screenshots.Count(item => item.QualityStatus == "pass");
        return $"""
            {GateStart}
            ## ADR-009 candidate gate

            Candidate capture `{summary.CaptureId}` is sealed but **not promoted**. Captured
            `{captured}/{screenshots.Length}`; quality-pass `{qualityPass}/{screenshots.Length}`;
            pixel differences `{determinism.PixelHashDifferenceCount}`; visual review
            `{determinism.ReviewStatus}`; capture duration `{summary.DurationSeconds:F1}` seconds.
            Independent security, code, test, and visual approval remain required before promotion.
            {GateEnd}
            """;
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<T>(
            await File.ReadAllTextAsync(path, cancellationToken),
            CaseInsensitiveJson)
        ?? throw new InvalidDataException($"{Path.GetFileName(path)} is invalid.");

    private static bool IsSafelyRedactedUrl(string value)
    {
        if (value == "[invalid-url]")
        {
            return true;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                return false;
            }

            var redacted = Uri.UnescapeDataString(parts[1]);
            if (redacted is not ("[REDACTED]" or "[REDACTED-SENSITIVE]"))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WriteTextAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, Utf8NoBom, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string ResolveChildPath(string root, string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var normalized = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(normalized)
            || normalized == ".."
            || normalized.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("evidence-path-escape");
        }

        CaptureIO.EnsureNoReparsePath(fullPath);
        return fullPath;
    }

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Key(ScreenshotRecord screenshot) =>
        $"{screenshot.TemplateKey}|{screenshot.Viewport}";

    private static bool IsSha256(string? value) =>
        value is not null
        && Regex.IsMatch(
            value,
            "^[0-9a-f]{64}$",
            RegexOptions.CultureInvariant);

    private static BaselineValidationResult Result(
        IEnumerable<string> reasons,
        IEnumerable<BaselineFileManifestEntry> files)
    {
        var orderedReasons = reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return new(
            orderedReasons.Length == 0,
            orderedReasons,
            files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private sealed record BaselineVerificationDocument(
        string SchemaVersion,
        string CaptureId,
        DateTimeOffset VerifiedInputCompletedAtUtc,
        bool Passed,
        int ScreenshotCount,
        int CapturedScreenshotCount,
        int QualityPassScreenshotCount,
        string RunBCaptureId,
        int PixelHashDifferenceCount,
        string ReviewStatus,
        IReadOnlyList<BaselineFileManifestEntry> Files);

    private sealed record ScreenshotNetworkPolicyDocument(
        string PolicyVersion,
        IReadOnlyList<string> AllowedMethods,
        IReadOnlyList<string> AllowedOrigins,
        IReadOnlyList<string> AllowedStaticHosts,
        IReadOnlyList<string> AllowedStaticResourceTypes,
        IReadOnlyList<string> BlockedCapabilities,
        IReadOnlyDictionary<string, IReadOnlyList<string>> PinnedDnsAnswers,
        IReadOnlyList<string> ChromiumHostResolverRules,
        int NavigationAttempts,
        int NavigationAttemptTimeoutSeconds,
        int ReadinessTimeoutSeconds,
        int CaptureTimeoutSeconds,
        int InterCaptureDelayMilliseconds,
        string Enforcement)
    {
        public string Status { get; init; } = CaptureFailureEvidence.CompleteStatus;

        public string? FailureReason { get; init; }
    }

    private sealed record FormsWidgetsDocument(
        bool NoSubmit,
        IReadOnlyList<FormObservation> Forms,
        IReadOnlyList<DynamicRegionObservation> DynamicRegions);
}
