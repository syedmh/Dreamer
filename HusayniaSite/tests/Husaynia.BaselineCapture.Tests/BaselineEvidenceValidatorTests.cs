using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class BaselineEvidenceValidatorTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Lazy<Task<BaselineValidationResult>>
        RetainedPreAdrValidation = new(
            () => new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                RetainedPreAdrFixturePath(),
                CancellationToken.None));

    [Fact]
    public async Task FinalizeIsDeterministicAndSealsExactTree()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var sealedFiles = new[]
            {
                "baseline-verification.json",
                "README.md",
                "checksums.sha256"
            };
            var before = sealedFiles.ToDictionary(
                file => file,
                file => CaptureIO.Sha256(File.ReadAllBytes(Path.Combine(candidate, file))),
                StringComparer.Ordinal);

            var second = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);
            var after = sealedFiles.ToDictionary(
                file => file,
                file => CaptureIO.Sha256(File.ReadAllBytes(Path.Combine(candidate, file))),
                StringComparer.Ordinal);

            Assert.True(second.Passed, string.Join(',', second.ReasonCodes));
            Assert.Equal(before, after);
            Assert.Contains(
                second.Files,
                file => file.RelativePath == "baseline-verification.json");
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ExtraUncheckedOrStaleChecksumFileFailsReadOnlyValidation()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(candidate, "unexpected.txt"), "stale");

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason == "unexpected-evidence-file:unexpected.txt");
            Assert.Contains("checksum-coverage-mismatch", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task StaleAllowedDiffFileIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var diffDirectory = Path.Combine(candidate, "screenshot-diffs");
            Directory.CreateDirectory(diffDirectory);
            await File.WriteAllTextAsync(Path.Combine(diffDirectory, "stale.txt"), "stale");

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("visual-diff-file-set-mismatch", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task CapturedOverflowCannotBeSealedOrPromoted()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var screenshotsPath = Path.Combine(candidate, "screenshots.json");
            var rows = JsonSerializer.Deserialize<ScreenshotRecord[]>(
                await File.ReadAllTextAsync(screenshotsPath),
                CaseInsensitiveJson)!;
            rows[0] = rows[0] with
            {
                Status = "captured",
                QualityStatus = "fail",
                QualityReasonCodes = ["horizontal-overflow"],
                HorizontalOverflow = true,
                DocumentScrollWidth = rows[0].ActualViewportWidth + 82
            };
            await CaptureIO.WriteJsonAsync(screenshotsPath, rows, CancellationToken.None);
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith("horizontal-overflow:", StringComparison.Ordinal));
            Assert.Contains("screenshot-capture-or-quality-incomplete", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingPromotedEvidenceRemainsNonPromotableUntilRegenerated()
    {
        var baseline = Path.Combine(
            BaselineTestFixture.FindRepositoryRoot(),
            "evidence",
            "baseline");

        var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
            baseline,
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(
            result.ReasonCodes,
            reason => reason.Contains("baseline-verification", StringComparison.Ordinal)
                || reason.Contains("screenshot", StringComparison.Ordinal)
                || reason.Contains("determinism", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyntheticOnePngForAllKeysIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var screenshotsPath = Path.Combine(candidate, "screenshots.json");
            var rows = JsonSerializer.Deserialize<ScreenshotRecord[]>(
                await File.ReadAllTextAsync(screenshotsPath),
                CaseInsensitiveJson)!;
            var source = Path.Combine(
                candidate,
                rows[0].SavedAs!.Replace('/', Path.DirectorySeparatorChar));
            var png = await File.ReadAllBytesAsync(source);
            var inspection = PngArtifactInspector.Inspect(png);
            for (var index = 0; index < rows.Length; index++)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(
                        candidate,
                        rows[index].SavedAs!.Replace('/', Path.DirectorySeparatorChar)),
                    png);
                rows[index] = rows[index] with
                {
                    Sha256 = inspection.Sha256,
                    PngBytes = inspection.Length,
                    PngWidth = inspection.Width,
                    PngHeight = inspection.Height,
                    ByteEntropy = inspection.ByteEntropy
                };
            }

            await CaptureIO.WriteJsonAsync(screenshotsPath, rows, CancellationToken.None);
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("screenshot-png-reused-across-keys", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptPngIsRejectedEvenWhenJsonClaimsPass()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var rows = JsonSerializer.Deserialize<ScreenshotRecord[]>(
                await File.ReadAllTextAsync(Path.Combine(candidate, "screenshots.json")),
                CaseInsensitiveJson)!;
            var path = Path.Combine(
                candidate,
                rows[0].SavedAs!.Replace('/', Path.DirectorySeparatorChar));
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[40] ^= 0x5a;
            await File.WriteAllBytesAsync(path, bytes);
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith("screenshot-png-invalid:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task OrphanTerminalAndMissingPerKeyLedgerAreRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var missingKey = decisions[0].CaptureKey;
            decisions.RemoveAll(item => item.CaptureKey == missingKey);
            decisions.Add(decisions[0] with
            {
                RequestId = "orphan-terminal",
                EventType = "response"
            });
            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith("network-decision-count:", StringComparison.Ordinal));
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    $"network-attempt-ledger-missing:{missingKey}:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyVerificationFileListIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "baseline-verification.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["files"] = new JsonArray();
            await File.WriteAllTextAsync(path, root.ToJsonString());
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("baseline-verification-file-list-invalid", result.ReasonCodes);
            Assert.Contains("baseline-verification-file-coverage-mismatch", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedSealIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(candidate, "baseline-verification.json"),
                "{");
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith("evidence-parse-failed:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task RepresentativeUrlMismatchIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshots.json");
            var rows = JsonSerializer.Deserialize<ScreenshotRecord[]>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            rows[0] = rows[0] with { Url = "https://www.husaynia.org/not-approved/" };
            await CaptureIO.WriteJsonAsync(path, rows, CancellationToken.None);
            await CaptureIO.WriteChecksumsAsync(candidate, CancellationToken.None);

            var result = await new BaselineEvidenceValidator().ValidateReadOnlyAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "screenshot-approved-tuple-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task MissingRouteSchemaVersionCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "route-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root.Remove("schemaVersion");
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "artifact-root-field-invalid:route-manifest.json:schemaVersion",
                result.ReasonCodes);
            Assert.False(File.Exists(Path.Combine(candidate, "baseline-verification.json")));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ResponseTerminalWithoutStatusCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var index = decisions.FindIndex(item => item.EventType == "response");
            decisions[index] = decisions[index] with { ResponseStatus = null };
            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "network-response-terminal-invalid:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticAttemptAbortTerminalIsLifecycleNotBrowserFailure()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var index = decisions.FindIndex(item => item.EventType == "response");
            var requestId = decisions[index].RequestId;
            decisions[index] = decisions[index] with
            {
                EventType = "lifecycle",
                ReasonCode = "attempt-aborted-before-browser-terminal",
                ResponseStatus = null,
                Failure = "capture-attempt-aborted"
            };
            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "network-main-frame-success-missing:",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                $"network-terminal-count:{decisions[index].CaptureKey}:{decisions[index].Attempt}:{requestId}",
                result.ReasonCodes);
            Assert.DoesNotContain(
                $"network-terminal-correlation-mismatch:{requestId}",
                result.ReasonCodes);
            Assert.DoesNotContain(
                $"network-failure-terminal-invalid:{requestId}",
                result.ReasonCodes);
            Assert.DoesNotContain(
                "network-decision-unclassified",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task AttemptAbortLifecycleTerminalRejectsResponseStatus()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var index = decisions.FindIndex(item => item.EventType == "response");
            var requestId = decisions[index].RequestId;
            decisions[index] = decisions[index] with
            {
                EventType = "lifecycle",
                ReasonCode = "attempt-aborted-before-browser-terminal",
                ResponseStatus = 200,
                Failure = "capture-attempt-aborted"
            };
            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"network-attempt-abort-terminal-invalid:{requestId}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task RouteCountDiagnosticsRejectNonQueryDelta()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "capture-summary.json");
            var summary = JsonSerializer.Deserialize<CaptureSummary>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            summary = summary with
            {
                DiscoveredUrlCount = summary.RouteCount + 1,
                ManifestPathCount = summary.RouteCount,
                QueryEndpointExcludedByFrozenSchemaCount = 1
            };
            await CaptureIO.WriteJsonAsync(path, summary, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "route-count-delta-not-query-only",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task StaleRunAHashBindingCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-determinism.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["screenshotHashes"]![0]!["runASha256"] = new string('0', 64);
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "determinism-run-a-hash-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task SameControlledRunCaptureIdCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-determinism.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["runBCaptureId"] = root["runACaptureId"]!.GetValue<string>();
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "controlled-run-capture-id-not-distinct",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyRunBCaptureIdCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-determinism.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["runBCaptureId"] = string.Empty;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "controlled-run-capture-id-not-distinct",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task RunAProvenanceOutsideSummaryCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var summaryPath = Path.Combine(candidate, "capture-summary.json");
            var summary = JsonSerializer.Deserialize<CaptureSummary>(
                await File.ReadAllTextAsync(summaryPath),
                CaseInsensitiveJson)!;
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(
                    Path.Combine(
                        candidate,
                        "screenshot-capture-provenance.json")),
                CaseInsensitiveJson)!;
            await CaptureIO.WriteJsonAsync(
                summaryPath,
                summary with
                {
                    CompletedAtUtc = provenance.CapturedAtUtc.AddSeconds(-1),
                    DurationSeconds =
                        (provenance.CapturedAtUtc.AddSeconds(-1)
                            - summary.StartedAtUtc).TotalSeconds
                },
                CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "capture-summary-lineage-invalid",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunBCompletedAtMustBeStrictlyAfterRunAWhenSealing(
        int secondsFromRunA)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var summaryPath = Path.Combine(candidate, "capture-summary.json");
            var summary = JsonSerializer.Deserialize<CaptureSummary>(
                await File.ReadAllTextAsync(summaryPath),
                CaseInsensitiveJson)!;
            var adjustedSummary = summary with
            {
                CompletedAtUtc = summary.StartedAtUtc.AddMinutes(2),
                DurationSeconds = 120
            };
            await CaptureIO.WriteJsonAsync(
                summaryPath,
                adjustedSummary,
                CancellationToken.None);

            var determinismPath = Path.Combine(
                candidate,
                "screenshot-determinism.json");
            var determinism = JsonNode.Parse(
                await File.ReadAllTextAsync(determinismPath))!.AsObject();
            var runBCapturedAt = DateTimeOffset.Parse(
                determinism["runBCapturedAtUtc"]!.GetValue<string>(),
                CultureInfo.InvariantCulture);
            determinism["runAStartedAtUtc"] =
                adjustedSummary.StartedAtUtc.ToString("O");
            determinism["runACompletedAtUtc"] =
                adjustedSummary.CompletedAtUtc.ToString("O");
            determinism["runBStartedAtUtc"] =
                runBCapturedAt.AddSeconds(-1).ToString("O");
            determinism["runBCompletedAtUtc"] =
                adjustedSummary.CompletedAtUtc
                    .AddSeconds(secondsFromRunA)
                    .ToString("O");
            determinism["comparedAtUtc"] =
                adjustedSummary.CompletedAtUtc.AddMinutes(1).ToString("O");
            await File.WriteAllTextAsync(
                determinismPath,
                determinism.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "controlled-run-completed-at-not-after",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFirstDiscoveryMediaRefCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var manifestPath = Path.Combine(
                candidate,
                "migration-import-manifest.json");
            var manifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var content = manifest["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["kind"]!.GetValue<string>() == "content");
            var sourceKey = content["sourceKey"]!.GetValue<string>();
            var expected = await ExpectedFirstDiscoveryMediaRefsAsync(
                candidate,
                content["sourceUri"]!.GetValue<string>());
            Assert.NotEmpty(expected);
            Assert.Equal(
                expected,
                content["mediaRefs"]!.AsArray()
                    .Select(node => node!.GetValue<string>())
                    .ToArray());
            content["mediaRefs"] = new JsonArray();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"import-candidate-media-refs-mismatch:{sourceKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFirstDiscoveryReligiousMediaRefCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var manifestPath = Path.Combine(
                candidate,
                "migration-import-manifest.json");
            var manifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var content = manifest["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["kind"]!.GetValue<string>() == "content");
            var sourceUri = content["sourceUri"]!.GetValue<string>();
            content["kind"] = "religious-content";
            content["sourceKey"] = CaptureIO.StableKey(
                "source",
                $"religious-content:{sourceUri}");
            content["targetKey"] = CaptureIO.StableKey(
                "target-religious-content",
                sourceUri);
            var sourceKey = content["sourceKey"]!.GetValue<string>();
            var expected = await ExpectedFirstDiscoveryMediaRefsAsync(
                candidate,
                sourceUri);
            Assert.NotEmpty(expected);
            content["mediaRefs"] = new JsonArray();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"import-candidate-media-refs-mismatch:{sourceKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ExtraFirstDiscoveryMediaRefCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var manifestPath = Path.Combine(
                candidate,
                "migration-import-manifest.json");
            var manifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var content = manifest["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["kind"]!.GetValue<string>() == "content");
            var sourceKey = content["sourceKey"]!.GetValue<string>();
            var expected = await ExpectedFirstDiscoveryMediaRefsAsync(
                candidate,
                content["sourceUri"]!.GetValue<string>());
            Assert.NotEmpty(expected);
            content["mediaRefs"]!.AsArray().Add(sourceKey);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"import-candidate-media-refs-mismatch:{sourceKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task UnreferencedAssetPngIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            File.Copy(
                Path.Combine(candidate, "assets", "fixture.png"),
                Path.Combine(candidate, "assets", "stale.png"));
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "unreferenced-evidence-file:assets/stale.png",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedReferencedAssetPngIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "assets", "fixture.png");
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[40] ^= 0x5a;
            await File.WriteAllBytesAsync(path, bytes);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "evidence-png-invalid:assets/fixture.png",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public void EvidencePngAggregateLimitIsBounded()
    {
        Assert.True(BaselineEvidenceValidator.EnsureEvidencePngAggregateWithinLimit(
            [1024, 2048],
            out var accepted));
        Assert.Equal(3072, accepted);
        Assert.False(BaselineEvidenceValidator.EnsureEvidencePngAggregateWithinLimit(
            [BaselineEvidenceValidator.MaximumEvidencePngBytes, 1],
            out var rejected));
        Assert.True(rejected > BaselineEvidenceValidator.MaximumEvidencePngBytes);
    }

    [Fact]
    public async Task EmptyRouteArrayCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "route-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["routes"] = new JsonArray();
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("route-manifest-schema-invalid", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task StaleVisualReviewWithoutPixelDifferencesIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(candidate, "screenshot-visual-review.json"),
                "[]");
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("visual-review-unexpected", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing", "visual-review-json-invalid")]
    [InlineData("default", "visual-review-invalid:home|mobile-320x568")]
    [InlineData("malformed", "visual-review-json-invalid")]
    [InlineData("before-run-b", "visual-review-invalid:home|mobile-320x568")]
    [InlineData("future", "visual-review-invalid:home|mobile-320x568")]
    [InlineData("non-utc", "visual-review-invalid:home|mobile-320x568")]
    public async Task InvalidReviewedUtcCannotBeSealed(
        string timestampCase,
        string expectedReason)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await ConfigureVisualReviewAsync(candidate, timestampCase);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(expectedReason, result.ReasonCodes);
            Assert.False(File.Exists(Path.Combine(
                candidate,
                "baseline-verification.json")));
            Assert.False(File.Exists(Path.Combine(candidate, "checksums.sha256")));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Theory]
    [InlineData("equal-run-b")]
    [InlineData("after-run-b")]
    public async Task ReviewedUtcAtOrAfterRunBCompletionCanBeSealed(
        string timestampCase)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await ConfigureVisualReviewAsync(candidate, timestampCase);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.True(result.Passed, string.Join(',', result.ReasonCodes));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task NonPngFileInScreenshotDirectoryIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(candidate, "screenshots", "unchecked.txt"),
                "unchecked");
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "unexpected-evidence-file:screenshots/unchecked.txt",
                result.ReasonCodes);
            Assert.Contains("screenshot-file-set-mismatch", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ImportPayloadReferenceMustMatchCandidateChecksum()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "migration-import-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var contentCandidate = root["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .First(node => node["kind"]!.GetValue<string>() == "content");
            const string replacement = "raw/pages/substituted.html";
            Directory.CreateDirectory(Path.Combine(candidate, "raw", "pages"));
            await File.WriteAllTextAsync(
                Path.Combine(candidate, replacement.Replace('/', Path.DirectorySeparatorChar)),
                "substituted payload");
            contentCandidate["payloadRef"] = replacement;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "import-candidate-checksum-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ChecksumConsistentCrossCandidatePayloadSubstitutionIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var manifestPath = Path.Combine(candidate, "migration-import-manifest.json");
            var manifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var candidates = manifest["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .ToArray();
            var contentCandidate = candidates.Single(
                node => node["kind"]!.GetValue<string>() == "content");
            var mediaCandidate = candidates.Single(
                node => node["kind"]!.GetValue<string>() == "media");
            contentCandidate["payloadRef"] =
                mediaCandidate["payloadRef"]!.GetValue<string>();
            contentCandidate["sourceChecksum"] =
                mediaCandidate["sourceChecksum"]!.GetValue<string>();
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "import-candidate-payload-ref-mismatch:",
                    StringComparison.Ordinal));
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "import-candidate-checksum-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Theory]
    [InlineData("religious-content")]
    [InlineData("endpoint")]
    [InlineData("redirect")]
    [InlineData("metadata")]
    [InlineData("navigation")]
    [InlineData("form")]
    [InlineData("editable-setting")]
    [InlineData("media")]
    public async Task AllowedImportCandidateKindSubstitutionCannotBeSealed(
        string substitutedKind)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await SubstituteContentCandidateAsync(candidate, substitutedKind);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("import-candidate-set-mismatch", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task AllowedImportCandidateDecisionSubstitutionCannotBeSealed()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var manifestPath = Path.Combine(
                candidate,
                "migration-import-manifest.json");
            var manifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var content = manifest["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node =>
                    node["kind"]!.GetValue<string>() == "content");
            var sourceKey = content["sourceKey"]!.GetValue<string>();
            content["decision"] = "update";
            await File.WriteAllTextAsync(
                manifestPath,
                manifest.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"import-candidate-decision-mismatch:{sourceKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedImportCandidateDependencyCannotBeRemoved()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var manifestPath = Path.Combine(
                candidate,
                "migration-import-manifest.json");
            var manifest = JsonNode.Parse(
                await File.ReadAllTextAsync(manifestPath))!.AsObject();
            var metadata = manifest["candidates"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node =>
                    node["kind"]!.GetValue<string>() == "metadata");
            var sourceKey = metadata["sourceKey"]!.GetValue<string>();
            metadata["dependencyKeys"] = new JsonArray();
            await File.WriteAllTextAsync(
                manifestPath,
                manifest.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"import-candidate-dependency-keys-mismatch:{sourceKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task OptionalImportModeUsesDryRunDefault()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "migration-import-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root.Remove("mode");
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.True(result.Passed, string.Join(',', result.ReasonCodes));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedImportCandidateKindIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "migration-import-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var content = root["candidates"]!.AsArray()[0]!.AsObject();
            const string kind = "unsupported-content";
            var sourceUri = content["sourceUri"]!.GetValue<string>();
            content["kind"] = kind;
            content["sourceKey"] =
                CaptureIO.StableKey("source", $"{kind}:{sourceUri}");
            content["targetKey"] =
                CaptureIO.StableKey($"target-{kind}", sourceUri);
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("import-manifest-schema-invalid", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task NonCanonicalImportPlanIdIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "migration-import-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["planId"] = $"{{{Guid.NewGuid():D}}}";
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("import-manifest-schema-invalid", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task RouteManifestFieldsMustMatchCapturedSourceRecords()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "route-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["routes"]![0]!["routeId"] = "route-forged";
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "route-manifest-semantic-mismatch:route-forged",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task PublicEndpointSeedSitemapCannotBeForged()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var routePath = await AddPublicEndpointSeedAsync(candidate);
            var path = Path.Combine(candidate, "route-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var route = root["routes"]!.AsArray()
                .Single(item =>
                    item!["legacyPath"]!.GetValue<string>() == routePath)!;
            route["sitemap"] = true;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "route-manifest-semantic-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task RouteSitemapCannotOmitRetainedSitemapMembership()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await AddRetainedSitemapEvidenceAsync(candidate);
            var path = Path.Combine(candidate, "route-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var route = root["routes"]!.AsArray()
                .Single(item =>
                    item!["legacyPath"]!.GetValue<string>() == "/fixture/")!
                .AsObject();
            route["sitemap"] = false;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "route-manifest-semantic-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task SitemapIndexLocationDoesNotCreateRouteMembership()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await AddRetainedSitemapEvidenceAsync(candidate, sitemapIndex: true);

            var result = await new BaselineEvidenceValidator()
                .ValidateReadOnlyAsync(candidate, CancellationToken.None);
            var manifest = JsonSerializer.Deserialize<RouteManifest>(
                await File.ReadAllTextAsync(
                    Path.Combine(candidate, "route-manifest.json")),
                CaseInsensitiveJson)!;

            Assert.True(result.Passed, string.Join(Environment.NewLine, result.ReasonCodes));
            Assert.False(Assert.Single(
                manifest.Routes,
                route => route.LegacyPath == "/fixture/").Sitemap);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task SitemapRawBytesMustMatchSuccessfulHttpBinding()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            await AddRetainedSitemapEvidenceAsync(candidate);
            await File.AppendAllTextAsync(
                Path.Combine(candidate, "raw", "sitemaps", "sitemap.xml.xml"),
                "stale");
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator()
                .ValidateReadOnlyAsync(candidate, CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "sitemap-http-binding-invalid:raw/sitemaps/sitemap.xml.xml",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task RetainedPreV3ProducerBoundaryRoutesExposeInferredSitemapMismatch()
    {
        var fixture = RetainedPreAdrFixturePath();
        var manifest = JsonSerializer.Deserialize<RouteManifest>(
            await File.ReadAllTextAsync(
                Path.Combine(fixture, "route-manifest.json")),
            CaseInsensitiveJson)!;
        var records = JsonSerializer.Deserialize<HttpRecord[]>(
            await File.ReadAllTextAsync(
                Path.Combine(fixture, "http-inventory.json")),
            CaseInsensitiveJson)!;
        var expected = new[]
        {
            (Path: "/", Status: 200, Sitemap: false, MissingStatus: false),
            (Path: "/events/", Status: 200, Sitemap: false, MissingStatus: true),
            (Path: "/feed/", Status: 200, Sitemap: false, MissingStatus: true),
            (
                Path: "/page-sitemap.xml",
                Status: 200,
                Sitemap: true,
                MissingStatus: true)
        };
        var result = await RetainedPreAdrValidation.Value;

        foreach (var boundary in expected)
        {
            var route = Assert.Single(
                manifest.Routes,
                item => item.LegacyPath == boundary.Path);
            var record = Assert.Single(
                records,
                item => item.Path == boundary.Path);

            Assert.Equal(boundary.Status, route.ExpectedStatus);
            Assert.Equal(boundary.Sitemap, route.Sitemap);
            Assert.Equal(boundary.MissingStatus, record.Status is null);
            var incompleteReason =
                $"http-route-capture-incomplete:{record.Url}";
            if (boundary.MissingStatus)
            {
                Assert.Contains(incompleteReason, result.ReasonCodes);
            }
            else
            {
                Assert.DoesNotContain(incompleteReason, result.ReasonCodes);
            }

            if (route.Sitemap)
            {
                Assert.Contains(
                    $"route-manifest-semantic-mismatch:{route.RouteId}",
                    result.ReasonCodes);
            }
            else
            {
                Assert.DoesNotContain(
                    $"route-manifest-semantic-mismatch:{route.RouteId}",
                    result.ReasonCodes);
            }
        }
    }

    [Fact]
    public async Task RetainedPreV3ProducerRouteManifestIsNotAcceptedAsExactSitemapEvidence()
    {
        var result = await RetainedPreAdrValidation.Value;

        Assert.Contains(
            result.ReasonCodes,
            reason => reason.StartsWith(
                "route-manifest-semantic-mismatch:",
                StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FrozenRouteFieldMutations))]
    public async Task EveryFrozenRouteFieldIsReconstructedFromRetainedEvidence(
        string fieldName)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var routePath = "/fixture/";
            if (fieldName == "redirectTarget")
            {
                routePath = await AddRedirectRouteAsync(candidate);
            }

            var path = Path.Combine(candidate, "route-manifest.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var route = root["routes"]!.AsArray()
                .Single(item =>
                    item!["legacyPath"]!.GetValue<string>() == routePath)!
                .AsObject();
            MutateFrozenRouteField(route, fieldName);
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "route-manifest-semantic-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    public static TheoryData<string> FrozenRouteFieldMutations =>
        new()
        {
            "routeId",
            "legacyPath",
            "canonicalPath",
            "expectedStatus",
            "redirectTarget",
            "templateKey",
            "contentKey",
            "indexable",
            "sitemap",
            "metadataKey",
            "assetKeys",
            "dynamicRegionKeys",
            "evidenceRefs",
            "contentChecksum"
        };

    [Fact]
    public async Task RouteManifestCannotClaimSuccessForFailedSourceStatus()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "http-inventory.json");
            var records = JsonSerializer.Deserialize<HttpRecord[]>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            records[0] = records[0] with { Status = 500 };
            await CaptureIO.WriteJsonAsync(path, records, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "route-manifest-semantic-mismatch:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ForgedChromiumResolverMappingIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var policyPath = Path.Combine(
                candidate,
                "screenshot-network-policy.json");
            var policy = JsonNode.Parse(
                await File.ReadAllTextAsync(policyPath))!.AsObject();
            var rules = policy["chromiumHostResolverRules"]!.AsArray();
            rules[0] = rules[0]!.GetValue<string>()
                .Replace("93.184.216.34", "127.0.0.1", StringComparison.Ordinal);
            await File.WriteAllTextAsync(policyPath, policy.ToJsonString());

            var provenancePath = Path.Combine(
                candidate,
                "screenshot-capture-provenance.json");
            var provenance = JsonNode.Parse(
                await File.ReadAllTextAsync(provenancePath))!.AsObject();
            provenance["chromiumHostResolverRules"]![0] =
                rules[0]!.GetValue<string>();
            await File.WriteAllTextAsync(
                provenancePath,
                provenance.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith(
                    "network-policy-resolver-rule-invalid:",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task SafeRequestRelabeledBlockedIsRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var index = decisions.FindIndex(item => item.EventType == "request");
            var request = decisions[index];
            decisions[index] = request with
            {
                Decision = "block",
                ReasonCode = "forged-block"
            };
            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"network-request-decision-invalid:{request.RequestId}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task AttemptCountCannotHideRetainedAttemptHistory()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var firstKey = decisions[0].CaptureKey;
            var added = decisions
                .Where(item => item.CaptureKey == firstKey)
                .Select(item => item with
                {
                    Attempt = 2,
                    RequestId = item.RequestId.Replace(
                        ":attempt:1:",
                        ":attempt:2:",
                        StringComparison.Ordinal)
                })
                .ToArray();
            decisions.AddRange(added);
            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"network-attempt-history-mismatch:{firstKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ForgedBrowserEnvironmentAndWorkspaceProvenanceAreRejected()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(
                candidate,
                "screenshot-capture-provenance.json");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            root["childEnvironmentKeys"] = new JsonArray(
                JsonValue.Create("PATH"),
                JsonValue.Create("NODE_OPTIONS"));
            root["childEnvironmentSanitized"] = false;
            root["browserWorkingDirectoryOutsideWorkspace"] = false;
            root["browserWorkingDirectoryEmpty"] = false;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "browser-environment-provenance-failed",
                result.ReasonCodes);
            Assert.Contains(
                "browser-working-directory-provenance-failed",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Theory]
    [InlineData("host-class")]
    [InlineData("origin-answer-rotation")]
    [InlineData("changed-pin-same-browser")]
    [InlineData("epoch-order")]
    [InlineData("orphan-epoch")]
    public async Task DnsEpochProvenanceEnforcesIndependentContextInvariants(
        string mutation)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(
                candidate,
                "screenshot-capture-provenance.json");
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var epochs = provenance.DnsContextEpochs.ToList();
            var origin = CaptureProfile.Approved.PrimaryOrigin.Host;
            switch (mutation)
            {
                case "host-class":
                    epochs[0] = epochs[0] with
                    {
                        Bindings = epochs[0].Bindings
                            .Select(binding => binding.Host == origin
                                ? binding with
                                {
                                    HostClass = nameof(TrustedHostClass.StaticResource)
                                }
                                : binding)
                            .ToArray()
                    };
                    break;
                case "origin-answer-rotation":
                    epochs[1] = epochs[1] with
                    {
                        Bindings = epochs[1].Bindings
                            .Select(binding => binding.Host == origin
                                ? binding with
                                {
                                    SelectedAddress = "93.184.216.35",
                                    CompleteObservedPublicSet = ["93.184.216.35"],
                                    AnswerSetSha256 = CaptureIO.Sha256(
                                        Encoding.UTF8.GetBytes("93.184.216.35"))
                                }
                                : binding)
                            .ToArray()
                    };
                    break;
                case "changed-pin-same-browser":
                    var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
                    epochs[1] = epochs[1] with
                    {
                        Bindings = epochs[1].Bindings
                            .Select(binding => binding.Host == staticHost
                                ? binding with
                                {
                                    SelectedAddress = "93.184.216.35",
                                    CompleteObservedPublicSet = ["93.184.216.35"],
                                    AnswerSetSha256 = CaptureIO.Sha256(
                                        Encoding.UTF8.GetBytes("93.184.216.35")),
                                    Rotated = true
                                }
                                : binding)
                            .ToArray()
                    };
                    break;
                case "epoch-order":
                    (epochs[0], epochs[1]) = (epochs[1], epochs[0]);
                    break;
                case "orphan-epoch":
                    epochs.Add(epochs[^1] with
                    {
                        Epoch = epochs.Count + 1,
                        Attempt = 2,
                        BrowserContextId = "orphan-context"
                    });
                    break;
            }

            await CaptureIO.WriteJsonAsync(
                path,
                provenance with { DnsContextEpochs = epochs },
                CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("dns-epoch-provenance-invalid", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task PublicStaticRotationKeepsInitialRunIdentityAndValidatesPerEpoch()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var provenancePath = Path.Combine(
                candidate,
                "screenshot-capture-provenance.json");
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(provenancePath),
                CaseInsensitiveJson)!;
            var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
            var epochs = provenance.DnsContextEpochs
                .Select((epoch, index) => index == 0
                    ? epoch
                    : epoch with
                    {
                        BrowserInstanceId = "rotated-browser",
                        Bindings = epoch.Bindings.Select(binding =>
                            binding.Host == staticHost
                                ? binding with
                                {
                                    SelectedAddress = "93.184.216.35",
                                    CompleteObservedPublicSet = ["93.184.216.35"],
                                    AnswerSetSha256 = CaptureIO.Sha256(
                                        Encoding.UTF8.GetBytes("93.184.216.35")),
                                    Rotated = index == 1
                                }
                                : binding).ToArray()
                    })
                .ToArray();
            var rotatedRules = epochs[1].Bindings
                .OrderBy(binding => binding.Host, StringComparer.Ordinal)
                .Select(binding => $"MAP {binding.Host} {binding.SelectedAddress}")
                .Append("MAP * ~NOTFOUND")
                .ToArray();
            await CaptureIO.WriteJsonAsync(
                provenancePath,
                provenance with
                {
                    DnsContextEpochs = epochs,
                    BrowserLaunches = provenance.BrowserLaunches.Append(
                        new BrowserLaunchEvidence(
                            "rotated-browser",
                            epochs[1].CreatedAtUtc.AddMilliseconds(-1),
                            TrustedEndpointPolicy.ComputeResolverMapSha256(
                                rotatedRules),
                            provenance.BrowserLaunches[0].BrowserInstanceId,
                            "resolver-map-changed"))
                        .ToArray()
                },
                CancellationToken.None);

            var decisionsPath = Path.Combine(
                candidate,
                "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<ScreenshotNetworkDecision[]>(
                await File.ReadAllTextAsync(decisionsPath),
                CaseInsensitiveJson)!;
            await CaptureIO.WriteJsonAsync(
                decisionsPath,
                decisions.Select(decision => decision.DnsEpoch > 1
                    ? decision with { BrowserInstanceId = "rotated-browser" }
                    : decision),
                CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.True(result.Passed, string.Join(Environment.NewLine, result.ReasonCodes));
            Assert.Equal(
                "93.184.216.34",
                provenance.PinnedDnsAnswers[staticHost][0]);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Theory]
    [InlineData("first-context-diff-no-relaunch")]
    [InlineData("unchanged-new-browser")]
    [InlineData("resolver-hash")]
    [InlineData("missing-launch")]
    [InlineData("duplicate-launch")]
    [InlineData("launch-after-context")]
    [InlineData("incorrect-rotated")]
    public async Task BrowserLaunchEvidenceRejectsInvalidLifecycleTransitions(
        string mutation)
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var provenancePath = Path.Combine(
                candidate,
                "screenshot-capture-provenance.json");
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(provenancePath),
                CaseInsensitiveJson)!;
            var epochs = provenance.DnsContextEpochs.ToArray();
            var launches = provenance.BrowserLaunches.ToList();
            var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
            string? replacementBrowser = null;
            switch (mutation)
            {
                case "first-context-diff-no-relaunch":
                    epochs[0] = epochs[0] with
                    {
                        Bindings = epochs[0].Bindings.Select(binding =>
                            binding.Host == staticHost
                                ? binding with
                                {
                                    SelectedAddress = "93.184.216.35",
                                    CompleteObservedPublicSet = ["93.184.216.35"],
                                    AnswerSetSha256 = CaptureIO.Sha256(
                                        Encoding.UTF8.GetBytes("93.184.216.35")),
                                    Rotated = false
                                }
                                : binding).ToArray()
                    };
                    break;
                case "unchanged-new-browser":
                    replacementBrowser = "unexplained-browser";
                    epochs[0] = epochs[0] with
                    {
                        BrowserInstanceId = replacementBrowser
                    };
                    launches.Add(new BrowserLaunchEvidence(
                        replacementBrowser,
                        epochs[0].CreatedAtUtc.AddSeconds(-1),
                        launches[0].ResolverMapSha256,
                        launches[0].BrowserInstanceId,
                        "resolver-map-changed"));
                    break;
                case "resolver-hash":
                    launches[0] = launches[0] with
                    {
                        ResolverMapSha256 = new string('0', 64)
                    };
                    break;
                case "missing-launch":
                    launches.Clear();
                    break;
                case "duplicate-launch":
                    launches.Add(launches[0]);
                    break;
                case "launch-after-context":
                    launches[0] = launches[0] with
                    {
                        LaunchedAtUtc = epochs[0].CreatedAtUtc.AddSeconds(1)
                    };
                    break;
                case "incorrect-rotated":
                    epochs[0] = epochs[0] with
                    {
                        Bindings = epochs[0].Bindings.Select(binding =>
                            binding.Host == staticHost
                                ? binding with { Rotated = true }
                                : binding).ToArray()
                    };
                    break;
            }

            await CaptureIO.WriteJsonAsync(
                provenancePath,
                provenance with
                {
                    DnsContextEpochs = epochs,
                    BrowserLaunches = launches
                },
                CancellationToken.None);
            if (replacementBrowser is not null)
            {
                var decisionsPath = Path.Combine(
                    candidate,
                    "screenshot-network-decisions.json");
                var decisions = JsonSerializer.Deserialize<ScreenshotNetworkDecision[]>(
                    await File.ReadAllTextAsync(decisionsPath),
                    CaseInsensitiveJson)!;
                await CaptureIO.WriteJsonAsync(
                    decisionsPath,
                    decisions.Select(decision => decision.DnsEpoch == 1
                        ? decision with { BrowserInstanceId = replacementBrowser }
                        : decision),
                    CancellationToken.None);
            }

            RemoveSeal(candidate);
            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("browser-launch-provenance-invalid", result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task BootstrapUnchangedAndBootstrapRotatedLaunchesAreAccepted()
    {
        var unchanged = await BaselineTestFixture.CreateValidCandidateAsync();
        var rotated = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var unchangedResult = await new BaselineEvidenceValidator().FinalizeAsync(
                unchanged,
                CancellationToken.None);
            Assert.True(
                unchangedResult.Passed,
                string.Join(Environment.NewLine, unchangedResult.ReasonCodes));

            var provenancePath = Path.Combine(
                rotated,
                "screenshot-capture-provenance.json");
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(provenancePath),
                CaseInsensitiveJson)!;
            var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
            var epochs = provenance.DnsContextEpochs
                .Select((epoch, index) => epoch with
                {
                    BrowserInstanceId = "first-context-rotated-browser",
                    Bindings = epoch.Bindings.Select(binding =>
                        binding.Host == staticHost
                            ? binding with
                            {
                                SelectedAddress = "93.184.216.35",
                                CompleteObservedPublicSet = ["93.184.216.35"],
                                AnswerSetSha256 = CaptureIO.Sha256(
                                    Encoding.UTF8.GetBytes("93.184.216.35")),
                                Rotated = index == 0
                            }
                            : binding).ToArray()
                })
                .ToArray();
            var rotatedRules = epochs[0].Bindings
                .OrderBy(binding => binding.Host, StringComparer.Ordinal)
                .Select(binding => $"MAP {binding.Host} {binding.SelectedAddress}")
                .Append("MAP * ~NOTFOUND")
                .ToArray();
            var launches = provenance.BrowserLaunches.Append(
                new BrowserLaunchEvidence(
                    "first-context-rotated-browser",
                    provenance.BrowserLaunches[0].LaunchedAtUtc,
                    TrustedEndpointPolicy.ComputeResolverMapSha256(rotatedRules),
                    provenance.BrowserLaunches[0].BrowserInstanceId,
                    "resolver-map-changed"))
                .ToArray();
            await CaptureIO.WriteJsonAsync(
                provenancePath,
                provenance with
                {
                    DnsContextEpochs = epochs,
                    BrowserLaunches = launches
                },
                CancellationToken.None);
            var decisionsPath = Path.Combine(
                rotated,
                "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<ScreenshotNetworkDecision[]>(
                await File.ReadAllTextAsync(decisionsPath),
                CaseInsensitiveJson)!;
            await CaptureIO.WriteJsonAsync(
                decisionsPath,
                decisions.Select(decision => decision with
                {
                    BrowserInstanceId = "first-context-rotated-browser"
                }),
                CancellationToken.None);
            RemoveSeal(rotated);

            var rotatedResult = await new BaselineEvidenceValidator().FinalizeAsync(
                rotated,
                CancellationToken.None);
            Assert.True(
                rotatedResult.Passed,
                string.Join(Environment.NewLine, rotatedResult.ReasonCodes));
        }
        finally
        {
            Directory.Delete(unchanged, recursive: true);
            Directory.Delete(rotated, recursive: true);
        }
    }

    [Fact]
    public async Task ReplacementBrowserLaunchCannotPredatePredecessorLaunch()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var provenancePath = Path.Combine(
                candidate,
                "screenshot-capture-provenance.json");
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(provenancePath),
                CaseInsensitiveJson)!;
            var staticHost = CaptureProfile.Approved.StaticResources[0].Host;
            var replacementId = "causally-early-browser";
            var epochs = provenance.DnsContextEpochs
                .Select((epoch, index) => epoch with
                {
                    BrowserInstanceId = replacementId,
                    Bindings = epoch.Bindings.Select(binding =>
                        binding.Host == staticHost
                            ? binding with
                            {
                                SelectedAddress = "93.184.216.35",
                                CompleteObservedPublicSet = ["93.184.216.35"],
                                AnswerSetSha256 = CaptureIO.Sha256(
                                    Encoding.UTF8.GetBytes("93.184.216.35")),
                                Rotated = index == 0
                            }
                            : binding).ToArray()
                })
                .ToArray();
            var replacementRules = epochs[0].Bindings
                .OrderBy(binding => binding.Host, StringComparer.Ordinal)
                .Select(binding => $"MAP {binding.Host} {binding.SelectedAddress}")
                .Append("MAP * ~NOTFOUND")
                .ToArray();
            await CaptureIO.WriteJsonAsync(
                provenancePath,
                provenance with
                {
                    DnsContextEpochs = epochs,
                    BrowserLaunches = provenance.BrowserLaunches.Append(
                        new BrowserLaunchEvidence(
                            replacementId,
                            provenance.BrowserLaunches[0].LaunchedAtUtc
                                .AddMilliseconds(-1),
                            TrustedEndpointPolicy.ComputeResolverMapSha256(
                                replacementRules),
                            provenance.BrowserLaunches[0].BrowserInstanceId,
                            "resolver-map-changed"))
                        .ToArray()
                },
                CancellationToken.None);
            var decisionsPath = Path.Combine(
                candidate,
                "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<ScreenshotNetworkDecision[]>(
                await File.ReadAllTextAsync(decisionsPath),
                CaseInsensitiveJson)!;
            await CaptureIO.WriteJsonAsync(
                decisionsPath,
                decisions.Select(decision => decision with
                {
                    BrowserInstanceId = replacementId
                }),
                CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                "browser-launch-chronology-invalid",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task FinalAttemptRequiresSuccessfulMainFrameResponse()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var path = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            var firstKey = decisions[0].CaptureKey;
            for (var index = 0; index < decisions.Count; index++)
            {
                if (decisions[index].CaptureKey == firstKey
                    && decisions[index].EventType == "response")
                {
                    decisions[index] = decisions[index] with { ResponseStatus = 500 };
                }
            }

            await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                $"network-main-frame-success-missing:{firstKey}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    [Fact]
    public async Task ReparsePointInsideEvidenceTreeIsRejectedBeforeReads()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        var target = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-reparse-target-{Guid.NewGuid():N}");
        var link = Path.Combine(candidate, "unsafe-reparse");
        try
        {
            Directory.CreateDirectory(target);
            if (OperatingSystem.IsWindows())
            {
                using var process = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(
                        Environment.GetEnvironmentVariable("ComSpec")
                            ?? throw new InvalidOperationException("ComSpec missing"),
                        $"/d /c mklink /J \"{link}\" \"{target}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    })!;
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(link, target);
            }

            RemoveSeal(candidate);
            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains(
                result.ReasonCodes,
                reason => reason.StartsWith("reparse-point:", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            if (Directory.Exists(candidate))
            {
                Directory.Delete(candidate, recursive: true);
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NetworkPolicyAndAllowedRequestMustMatchCanonicalTrust()
    {
        var candidate = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var policyPath = Path.Combine(candidate, "screenshot-network-policy.json");
            var policy = JsonNode.Parse(await File.ReadAllTextAsync(policyPath))!.AsObject();
            policy["allowedMethods"]!.AsArray().Add("POST");
            await File.WriteAllTextAsync(policyPath, policy.ToJsonString());

            var decisionsPath = Path.Combine(candidate, "screenshot-network-decisions.json");
            var decisions = JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
                await File.ReadAllTextAsync(decisionsPath),
                CaseInsensitiveJson)!;
            var request = decisions.First(item =>
                item.EventType == "request" && item.Decision == "allow");
            for (var index = 0; index < decisions.Count; index++)
            {
                if (decisions[index].RequestId == request.RequestId)
                {
                    decisions[index] = decisions[index] with { Method = "POST" };
                }
            }

            await CaptureIO.WriteJsonAsync(
                decisionsPath,
                decisions,
                CancellationToken.None);
            RemoveSeal(candidate);

            var result = await new BaselineEvidenceValidator().FinalizeAsync(
                candidate,
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("network-policy-canonical-mismatch", result.ReasonCodes);
            Assert.Contains(
                $"network-request-decision-invalid:{request.RequestId}",
                result.ReasonCodes);
        }
        finally
        {
            Directory.Delete(candidate, recursive: true);
        }
    }

    private static void RemoveSeal(string candidate)
    {
        File.Delete(Path.Combine(candidate, "baseline-verification.json"));
        File.Delete(Path.Combine(candidate, "checksums.sha256"));
    }

    private static async Task ConfigureVisualReviewAsync(
        string candidate,
        string timestampCase)
    {
        var screenshots = JsonSerializer.Deserialize<ScreenshotRecord[]>(
            await File.ReadAllTextAsync(Path.Combine(candidate, "screenshots.json")),
            CaseInsensitiveJson)!;
        var screenshot = screenshots.Single(item =>
            item.TemplateKey == "home"
            && item.Viewport == "mobile-320x568");
        var key = $"{screenshot.TemplateKey}|{screenshot.Viewport}";
        var determinismPath = Path.Combine(
            candidate,
            "screenshot-determinism.json");
        var determinism = JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(determinismPath),
            CaseInsensitiveJson)!;
        var hashes = determinism.ScreenshotHashes.Single(item => item.Key == key);
        var diffRelative = $"screenshot-diffs/{CaptureIO.SafeFileName(key)}.png";
        var diffPath = Path.Combine(
            candidate,
            diffRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(diffPath)!);
        File.Copy(
            Path.Combine(
                candidate,
                screenshot.SavedAs!.Replace('/', Path.DirectorySeparatorChar)),
            diffPath);
        var difference = new ScreenshotVisualDifference(
            key,
            hashes.RunASha256,
            hashes.RunBSha256,
            diffRelative,
            1,
            0.000001,
            new VisualDiffBounds(0, 0, 0, 0));
        await CaptureIO.WriteJsonAsync(
            determinismPath,
            determinism with
            {
                PixelHashDifferenceCount = 1,
                PixelDifferences = [difference],
                ReviewStatus = "accepted",
                Passed = true
            },
            CancellationToken.None);
        var review = new JsonObject
        {
            ["key"] = key,
            ["runASha256"] = hashes.RunASha256,
            ["runBSha256"] = hashes.RunBSha256,
            ["unmaskedDiffPath"] = diffRelative,
            ["maskVersion"] = null,
            ["rectangles"] = null,
            ["reviewer"] = "independent-reviewer",
            ["decision"] = "accept",
            ["rationale"] = "Fixture intentionally exercises visual review sealing."
        };
        SetReviewedUtc(
            review,
            timestampCase,
            determinism.RunBCompletedAtUtc);
        await File.WriteAllTextAsync(
            Path.Combine(candidate, "screenshot-visual-review.json"),
            new JsonArray(review).ToJsonString());
    }

    private static void SetReviewedUtc(
        JsonObject review,
        string timestampCase,
        DateTimeOffset runBCompletedAtUtc)
    {
        switch (timestampCase)
        {
            case "equal-run-b":
                review["reviewedUtc"] = runBCompletedAtUtc.ToString("O");
                break;
            case "after-run-b":
                review["reviewedUtc"] =
                    runBCompletedAtUtc.AddSeconds(1).ToString("O");
                break;
            case "missing":
                break;
            case "default":
                review["reviewedUtc"] = "0001-01-01T00:00:00+00:00";
                break;
            case "malformed":
                review["reviewedUtc"] = "not-a-timestamp";
                break;
            case "before-run-b":
                review["reviewedUtc"] =
                    runBCompletedAtUtc.AddTicks(-1).ToString("O");
                break;
            case "future":
                review["reviewedUtc"] =
                    DateTimeOffset.UtcNow.AddDays(1).ToString("O");
                break;
            case "non-utc":
                review["reviewedUtc"] = "2026-08-15T20:00:00+01:00";
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(timestampCase),
                    timestampCase,
                    "Unknown reviewedUtc test case.");
        }
    }

    private static void MutateFrozenRouteField(
        JsonObject route,
        string fieldName)
    {
        switch (fieldName)
        {
            case "routeId":
                route[fieldName] = "route-forged";
                break;
            case "legacyPath":
            case "canonicalPath":
                route[fieldName] = "/forged/";
                break;
            case "expectedStatus":
                route[fieldName] = 404;
                break;
            case "redirectTarget":
                route[fieldName] = "/forged/";
                break;
            case "templateKey":
                route[fieldName] = "forged-template";
                break;
            case "contentKey":
                route[fieldName] = CaptureIO.StableKey(
                    "content",
                    "/forged/");
                break;
            case "indexable":
            case "sitemap":
                route[fieldName] = !route[fieldName]!.GetValue<bool>();
                break;
            case "metadataKey":
                route[fieldName] = CaptureIO.StableKey(
                    "metadata",
                    "/forged/");
                break;
            case "assetKeys":
                route[fieldName] = new JsonArray();
                break;
            case "dynamicRegionKeys":
                route[fieldName] = new JsonArray("dynamic-forged");
                break;
            case "evidenceRefs":
                route[fieldName] = new JsonArray("http-inventory.json");
                break;
            case "contentChecksum":
                route[fieldName] = new string('0', 64);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(fieldName),
                    fieldName,
                    "Unknown frozen route field.");
        }
    }

    private static async Task<string> AddRedirectRouteAsync(string candidate)
    {
        const string legacyUrl =
            "https://www.husaynia.org/legacy-fixture/";
        const string legacyPath = "/legacy-fixture/";
        const string targetUrl = "https://www.husaynia.org/fixture/";
        var record = new HttpRecord(
            legacyUrl,
            legacyPath,
            200,
            targetUrl,
            [new RedirectHop(legacyUrl, 301, targetUrl)],
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>());

        var httpPath = Path.Combine(candidate, "http-inventory.json");
        var httpRecords = JsonSerializer.Deserialize<HttpRecord[]>(
            await File.ReadAllTextAsync(httpPath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            httpPath,
            httpRecords
                .Append(record)
                .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CancellationToken.None);

        var summaryPath = Path.Combine(candidate, "capture-summary.json");
        var summary = JsonSerializer.Deserialize<CaptureSummary>(
            await File.ReadAllTextAsync(summaryPath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            summaryPath,
            summary with
            {
                RouteCount = summary.RouteCount + 1,
                SuccessfulRouteCount = summary.SuccessfulRouteCount + 1,
                RedirectCount = summary.RedirectCount + 1,
                NetworkRequestCount = summary.NetworkRequestCount + 1
            },
            CancellationToken.None);

        var routePath = Path.Combine(candidate, "route-manifest.json");
        var routeManifest = JsonSerializer.Deserialize<RouteManifest>(
            await File.ReadAllTextAsync(routePath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            routePath,
            routeManifest with
            {
                Routes = routeManifest.Routes
                    .Append(new RouteEntry(
                        CaptureIO.StableKey("route", legacyPath),
                        legacyPath,
                        legacyPath,
                        301,
                        "/fixture/",
                        "content-page",
                        null,
                        true,
                        false,
                        null,
                        [],
                        [],
                        ["http-inventory.json", "capture-summary.json"],
                        null))
                    .OrderBy(item => item.LegacyPath, StringComparer.Ordinal)
                    .ToArray()
            },
            CancellationToken.None);

        await AddImportCandidateAsync(
            candidate,
            new ImportCandidate(
                CaptureIO.StableKey("source", $"redirect:{legacyUrl}"),
                summary.CaptureId,
                CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        record,
                        CaptureIO.JsonOptions)),
                "redirect",
                legacyUrl,
                CaptureIO.StableKey("target-redirect", legacyUrl),
                legacyPath,
                "http-inventory.json",
                [],
                [],
                null,
                "update",
                "Legacy redirect must be reconciled to a single permanent target."));
        await ResealAsync(candidate);
        return legacyPath;
    }

    private static async Task AddRetainedSitemapEvidenceAsync(
        string candidate,
        bool sitemapIndex = false)
    {
        const string sitemapUrl = "https://www.husaynia.org/sitemap.xml";
        const string sitemapPath = "/sitemap.xml";
        const string retainedRelative = "raw/sitemaps/sitemap.xml.xml";
        const string endpointRelative = retainedRelative;
        var sitemapBytes = System.Text.Encoding.UTF8.GetBytes(
            sitemapIndex
                ? """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.husaynia.org/fixture/</loc></sitemap>
            </sitemapindex>
            """
                : """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.husaynia.org/fixture/</loc></url>
            </urlset>
            """);
        var sitemapSha = CaptureIO.Sha256(sitemapBytes);
        Directory.CreateDirectory(Path.Combine(candidate, "raw", "sitemaps"));
        await File.WriteAllBytesAsync(
            Path.Combine(
                candidate,
                retainedRelative.Replace(
                    '/',
                    Path.DirectorySeparatorChar)),
            sitemapBytes);

        var record = new HttpRecord(
            sitemapUrl,
            sitemapPath,
            200,
            sitemapUrl,
            [],
            "application/xml",
            sitemapBytes.LongLength,
            sitemapSha,
            endpointRelative,
            null,
            new Dictionary<string, string>());
        var httpPath = Path.Combine(candidate, "http-inventory.json");
        var httpRecords = JsonSerializer.Deserialize<HttpRecord[]>(
            await File.ReadAllTextAsync(httpPath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            httpPath,
            httpRecords
                .Append(record)
                .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CancellationToken.None);

        var summaryPath = Path.Combine(candidate, "capture-summary.json");
        var summary = JsonSerializer.Deserialize<CaptureSummary>(
            await File.ReadAllTextAsync(summaryPath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            summaryPath,
            summary with
            {
                SitemapCount = summary.SitemapCount + 1,
                RouteCount = summary.RouteCount + 1,
                SuccessfulRouteCount = summary.SuccessfulRouteCount + 1,
                NetworkRequestCount = summary.NetworkRequestCount + 1
            },
            CancellationToken.None);

        var routePath = Path.Combine(candidate, "route-manifest.json");
        var routeManifest = JsonSerializer.Deserialize<RouteManifest>(
            await File.ReadAllTextAsync(routePath),
            CaseInsensitiveJson)!;
        var routes = routeManifest.Routes
            .Select(route =>
                route.LegacyPath == "/fixture/"
                    ? route with { Sitemap = !sitemapIndex }
                    : route)
            .Append(new RouteEntry(
                CaptureIO.StableKey("route", sitemapPath),
                sitemapPath,
                sitemapPath,
                200,
                null,
                "machine-feed",
                CaptureIO.StableKey("content", sitemapPath),
                true,
                false,
                null,
                [],
                [],
                [
                    endpointRelative,
                    "http-inventory.json",
                    "capture-summary.json"
                ],
                sitemapSha))
            .OrderBy(item => item.LegacyPath, StringComparer.Ordinal)
            .ToArray();
        await CaptureIO.WriteJsonAsync(
            routePath,
            routeManifest with { Routes = routes },
            CancellationToken.None);

        await AddImportCandidateAsync(
            candidate,
            new ImportCandidate(
                CaptureIO.StableKey("source", $"endpoint:{sitemapUrl}"),
                summary.CaptureId,
                sitemapSha,
                "endpoint",
                sitemapUrl,
                CaptureIO.StableKey("target-endpoint", sitemapUrl),
                sitemapPath,
                endpointRelative,
                [],
                [],
                null,
                "update",
                "Generated public endpoint must be recreated rather than copied as editable content."));
        await ResealAsync(candidate);
    }

    private static async Task<string> AddPublicEndpointSeedAsync(
        string candidate)
    {
        const string url = "https://www.husaynia.org/";
        const string path = "/";
        var record = new HttpRecord(
            url,
            path,
            200,
            url,
            [],
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>());
        var httpPath = Path.Combine(candidate, "http-inventory.json");
        var httpRecords = JsonSerializer.Deserialize<HttpRecord[]>(
            await File.ReadAllTextAsync(httpPath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            httpPath,
            httpRecords
                .Append(record)
                .OrderBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            CancellationToken.None);

        var summaryPath = Path.Combine(candidate, "capture-summary.json");
        var summary = JsonSerializer.Deserialize<CaptureSummary>(
            await File.ReadAllTextAsync(summaryPath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            summaryPath,
            summary with
            {
                RouteCount = summary.RouteCount + 1,
                SuccessfulRouteCount = summary.SuccessfulRouteCount + 1,
                NetworkRequestCount = summary.NetworkRequestCount + 1
            },
            CancellationToken.None);

        var routePath = Path.Combine(candidate, "route-manifest.json");
        var routeManifest = JsonSerializer.Deserialize<RouteManifest>(
            await File.ReadAllTextAsync(routePath),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            routePath,
            routeManifest with
            {
                Routes = routeManifest.Routes
                    .Append(new RouteEntry(
                        CaptureIO.StableKey("route", path),
                        path,
                        path,
                        200,
                        null,
                        BaselineCaptureService.ClassifyTemplate(path),
                        null,
                        true,
                        false,
                        null,
                        [],
                        [],
                        ["http-inventory.json", "capture-summary.json"],
                        null))
                    .OrderBy(item => item.LegacyPath, StringComparer.Ordinal)
                    .ToArray()
            },
            CancellationToken.None);

        await AddImportCandidateAsync(
            candidate,
            new ImportCandidate(
                CaptureIO.StableKey("source", $"endpoint:{url}"),
                summary.CaptureId,
                CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        record,
                        CaptureIO.JsonOptions)),
                "endpoint",
                url,
                CaptureIO.StableKey("target-endpoint", url),
                path,
                "http-inventory.json",
                [],
                [],
                null,
                "reject",
                "Captured endpoint was not importable: HTTP 200"));
        await ResealAsync(candidate);
        return path;
    }

    private static string RetainedPreAdrFixturePath() =>
        Path.Combine(
            BaselineTestFixture.FindRepositoryRoot(),
            "evidence",
            "baseline-runs",
            "adr009-20260816T0645Z-run-a");

    private static async Task AddImportCandidateAsync(
        string candidate,
        ImportCandidate addedCandidate)
    {
        var path = Path.Combine(candidate, "migration-import-manifest.json");
        var manifest = JsonSerializer.Deserialize<ImportManifest>(
            await File.ReadAllTextAsync(path),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            path,
            manifest with
            {
                Candidates = manifest.Candidates
                    .Append(addedCandidate)
                    .OrderBy(item => item.SourceKey, StringComparer.Ordinal)
                    .ToArray()
            },
            CancellationToken.None);
    }

    private static async Task ResealAsync(string candidate)
    {
        RemoveSeal(candidate);
        var result = await new BaselineEvidenceValidator().FinalizeAsync(
            candidate,
            CancellationToken.None);
        Assert.True(result.Passed, string.Join(',', result.ReasonCodes));
    }

    private static async Task SubstituteContentCandidateAsync(
        string candidate,
        string substitutedKind)
    {
        var manifestPath = Path.Combine(
            candidate,
            "migration-import-manifest.json");
        var manifest = JsonNode.Parse(
            await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var candidates = manifest["candidates"]!.AsArray();
        var contentIndex = candidates
            .Select((node, index) => new
            {
                Candidate = node!.AsObject(),
                Index = index
            })
            .Single(item =>
                item.Candidate["kind"]!.GetValue<string>() == "content")
            .Index;
        var content = candidates[contentIndex]!.AsObject();
        var sourceUri = content["sourceUri"]!.GetValue<string>();

        if (substitutedKind is "navigation" or "form" or "media")
        {
            candidates[contentIndex] = candidates
                .Single(node =>
                    node!["kind"]!.GetValue<string>() == substitutedKind)!
                .DeepClone();
        }
        else if (substitutedKind == "metadata")
        {
            var metadata = JsonSerializer.Deserialize<MetadataRecord[]>(
                await File.ReadAllTextAsync(
                    Path.Combine(candidate, "metadata-inventory.json")),
                CaseInsensitiveJson)!
                .Single(item => item.Url == sourceUri);
            var contentSourceKey = content["sourceKey"]!.GetValue<string>();
            content["kind"] = substitutedKind;
            content["sourceKey"] = CaptureIO.StableKey(
                "source",
                $"{substitutedKind}:{sourceUri}");
            content["sourceChecksum"] = CaptureIO.Sha256(
                JsonSerializer.SerializeToUtf8Bytes(
                    metadata,
                    CaptureIO.JsonOptions));
            content["targetKey"] = CaptureIO.StableKey(
                $"target-{substitutedKind}",
                sourceUri);
            content["payloadRef"] = "metadata-inventory.json";
            content["mediaRefs"] = new JsonArray();
            content["dependencyKeys"] = new JsonArray(contentSourceKey);
            content["decision"] = "update";
            content["conflictReason"] =
                "SEO metadata is reconciled independently from body content.";
        }
        else if (substitutedKind == "editable-setting")
        {
            var summary = JsonNode.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(candidate, "capture-summary.json")))!.AsObject();
            var sourceBaseUrl =
                summary["sourceBaseUrl"]!.GetValue<string>();
            var navigation = JsonNode.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(candidate, "navigation-inventory.json")))!
                .AsArray();
            var forms = JsonNode.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(candidate, "forms-widgets.json")))!.AsObject();
            content["kind"] = substitutedKind;
            content["sourceKey"] = CaptureIO.StableKey(
                "source",
                $"{substitutedKind}:site-settings");
            content["sourceChecksum"] = CaptureIO.Sha256(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        BaseUri = new Uri(sourceBaseUrl),
                        Navigation = navigation.Count,
                        DynamicRegions =
                            forms["dynamicRegions"]!.AsArray().Count
                    },
                    CaptureIO.JsonOptions));
            content["sourceUri"] = sourceBaseUrl;
            content["targetKey"] = CaptureIO.StableKey(
                $"target-{substitutedKind}",
                "site-settings");
            content["canonicalPath"] = "/";
            content["payloadRef"] = "forms-widgets.json";
            content["mediaRefs"] = new JsonArray();
            content["dependencyKeys"] = new JsonArray();
            content["decision"] = "conflict";
            content["conflictReason"] =
                "Provider credentials, payment configuration, form destinations and widget settings are not observable from public GET evidence.";
        }
        else
        {
            content["kind"] = substitutedKind;
            content["sourceKey"] = CaptureIO.StableKey(
                "source",
                $"{substitutedKind}:{sourceUri}");
            content["targetKey"] = CaptureIO.StableKey(
                $"target-{substitutedKind}",
                sourceUri);
            content["dependencyKeys"] = new JsonArray();

            if (substitutedKind == "religious-content")
            {
                content.Remove("conflictReason");
            }
            else if (substitutedKind == "endpoint")
            {
                content["mediaRefs"] = new JsonArray();
                content["decision"] = "update";
                content["conflictReason"] =
                    "Generated public endpoint must be recreated rather than copied as editable content.";
            }
            else
            {
                var records = JsonSerializer.Deserialize<HttpRecord[]>(
                    await File.ReadAllTextAsync(
                        Path.Combine(candidate, "http-inventory.json")),
                    CaseInsensitiveJson)!;
                var record = records.Single(item => item.Url == sourceUri);
                content["sourceChecksum"] = CaptureIO.Sha256(
                    JsonSerializer.SerializeToUtf8Bytes(
                        record,
                        CaptureIO.JsonOptions));
                content["payloadRef"] = "http-inventory.json";
                content["mediaRefs"] = new JsonArray();
                content["decision"] = "update";
                content["conflictReason"] =
                    "Legacy redirect must be reconciled to a single permanent target.";
            }
        }

        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString());
    }

    private static async Task<string[]> ExpectedFirstDiscoveryMediaRefsAsync(
        string candidate,
        string sourcePage)
    {
        var assets = JsonSerializer.Deserialize<List<AssetRecord>>(
            await File.ReadAllTextAsync(
                Path.Combine(candidate, "media-inventory.json")),
            CaseInsensitiveJson)!;
        var references = assets
            .GroupBy(asset => asset.Url, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(asset => asset.SourcePage)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        return BaselineCaptureService.SelectMediaRefsForSourcePage(
            assets,
            references,
            sourcePage);
    }
}
