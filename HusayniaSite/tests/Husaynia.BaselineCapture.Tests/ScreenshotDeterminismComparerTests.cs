using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Husaynia.BaselineCapture;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class ScreenshotDeterminismComparerTests
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Theory]
    [InlineData("\"key\"", "\"Key\"")]
    [InlineData("\"key\":", "\"extra\":true,\"key\":")]
    public async Task VisualReviewParserRejectsWrongCaseAndUnknownFields(
        string original,
        string replacement)
    {
        var json = ValidStrictReviewJson()
            .Replace(original, replacement, StringComparison.Ordinal);

        await AssertStrictReviewRejectedAsync(json);
    }

    [Fact]
    public async Task VisualReviewParserRejectsDuplicateNestedFieldsAndInvalidMaskPair()
    {
        var duplicateRectangle = ValidStrictReviewJson()
            .Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":0,\"x\":1,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic clock\"}]",
                StringComparison.Ordinal);
        var unpairedMask = ValidStrictReviewJson()
            .Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":null",
                StringComparison.Ordinal);

        await AssertStrictReviewRejectedAsync(duplicateRectangle);
        await AssertStrictReviewRejectedAsync(unpairedMask);
    }

    [Fact]
    public async Task VisualReviewParserRequiresTenFieldsAndValidMaskBounds()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-visual-review-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, ValidStrictReviewJson());
            var entry = Assert.Single(
                await ScreenshotVisualReviewParser.ParseAsync(
                    path,
                    CancellationToken.None));
            Assert.True(ScreenshotVisualReviewParser.IsMaskInBounds(entry, 10, 10));
            var atEdge = entry with
            {
                MaskVersion = "1",
                Rectangles =
                [
                    new VisualReviewRectangle(9, 9, 1, 1, "dynamic clock")
                ]
            };
            var outside = atEdge with
            {
                Rectangles =
                [
                    new VisualReviewRectangle(9, 9, 2, 1, "dynamic clock")
                ]
            };
            var below = atEdge with
            {
                Rectangles =
                [
                    new VisualReviewRectangle(9, 9, 1, 2, "dynamic clock")
                ]
            };

            Assert.True(ScreenshotVisualReviewParser.IsMaskInBounds(atEdge, 10, 10));
            Assert.False(ScreenshotVisualReviewParser.IsMaskInBounds(outside, 10, 10));
            Assert.False(ScreenshotVisualReviewParser.IsMaskInBounds(below, 10, 10));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string> InvalidMaskJsonMutations => new()
    {
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                $"\"maskVersion\":\"1\",\"rectangles\":[{string.Join(',', Enumerable.Repeat("{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic\"}", 65))}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"2\",\"rectangles\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":null,\"rectangles\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":-1,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":0,\"y\":-1,\"width\":1,\"height\":1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":0,\"y\":0,\"width\":0,\"height\":1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":-1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\" \"}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                $"\"maskVersion\":\"1\",\"rectangles\":[{{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"{new string('x', ScreenshotVisualReviewParser.MaximumTextLength + 1)}\"}}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"x\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic\",\"extra\":true}]",
                StringComparison.Ordinal),
            ValidStrictReviewJson().Replace(
                "\"maskVersion\":null,\"rectangles\":null",
                "\"maskVersion\":\"1\",\"rectangles\":[{\"X\":0,\"y\":0,\"width\":1,\"height\":1,\"rationale\":\"dynamic\"}]",
                StringComparison.Ordinal)
    };

    [Theory]
    [MemberData(nameof(InvalidMaskJsonMutations))]
    public async Task VisualReviewParserRejectsStrictMaskMatrix(string json)
    {
        await AssertStrictReviewRejectedAsync(json);
    }

    [Fact]
    public async Task MissingOrDuplicateApprovedKeyFailsBeforePixelComparison()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var secondRows = JsonSerializer.Deserialize<ScreenshotRecord[]>(
            await File.ReadAllTextAsync(Path.Combine(runs.RunB, "screenshots.json")),
            CaseInsensitiveJson)!;
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunB, "screenshots.json"),
            secondRows[..^1],
            CancellationToken.None);

        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            Path.Combine(runs.RunA, "screenshot-determinism.json"),
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        using var evidence = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(runs.RunA, "screenshot-determinism.json")));
        Assert.Contains(
            evidence.RootElement.GetProperty("metricDifferences").EnumerateArray(),
            item => item.GetString()!.Contains("missing-key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopiedRunAIsRejectedAsRunB()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        Directory.Delete(runs.RunB, recursive: true);
        CopyDirectory(runs.RunA, runs.RunB);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "controlled-run-capture-id-not-distinct",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task SameCaptureIdWithLaterTimestampsIsRejected()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var runAProvenance = await ReadProvenanceAsync(runs.RunA);
        var runBSummaryPath = Path.Combine(runs.RunB, "capture-summary.json");
        var runBProvenancePath = Path.Combine(
            runs.RunB,
            "screenshot-capture-provenance.json");
        var runBSummary = await ReadSummaryAsync(runs.RunB);
        var runBProvenance = await ReadProvenanceAsync(runs.RunB);
        await CaptureIO.WriteJsonAsync(
            runBSummaryPath,
            runBSummary with { CaptureId = runAProvenance.CaptureId },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            runBProvenancePath,
            runBProvenance with { CaptureId = runAProvenance.CaptureId },
            CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunB, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "controlled-run-capture-id-not-distinct",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task EmptyRunACaptureIdIsRejected()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var runASummary = await ReadSummaryAsync(runs.RunA);
        var runAProvenance = await ReadProvenanceAsync(runs.RunA);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunA, "capture-summary.json"),
            runASummary with { CaptureId = string.Empty },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunA, "screenshot-capture-provenance.json"),
            runAProvenance with { CaptureId = string.Empty },
            CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunA, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "controlled-run-capture-id-not-distinct",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task RunAProvenanceMustFallWithinItsSummary()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var runASummary = await ReadSummaryAsync(runs.RunA);
        var runAProvenance = await ReadProvenanceAsync(runs.RunA);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunA, "capture-summary.json"),
            runASummary with
            {
                CompletedAtUtc = runAProvenance.CapturedAtUtc.AddSeconds(-1),
                DurationSeconds =
                    (runAProvenance.CapturedAtUtc.AddSeconds(-1)
                        - runASummary.StartedAtUtc).TotalSeconds
            },
            CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunA, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "run-a:capture-summary-lineage-invalid",
            evidence.MetricDifferences);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunBCapturedAtMustBeStrictlyAfterRunA(int secondsFromRunA)
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var runAProvenance = await ReadProvenanceAsync(runs.RunA);
        var runBSummary = await ReadSummaryAsync(runs.RunB);
        var runBProvenance = await ReadProvenanceAsync(runs.RunB);
        var adjustedCapturedAt =
            runAProvenance.CapturedAtUtc.AddSeconds(secondsFromRunA);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunB, "capture-summary.json"),
            runBSummary with
            {
                StartedAtUtc = adjustedCapturedAt.AddSeconds(-1),
                CompletedAtUtc = adjustedCapturedAt.AddSeconds(1),
                DurationSeconds = 2
            },
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunB, "screenshot-capture-provenance.json"),
            runBProvenance with { CapturedAtUtc = adjustedCapturedAt },
            CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunB, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "controlled-run-captured-at-not-after",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task RunBCompletedAtMustBeStrictlyAfterRunA()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var runASummary = await ReadSummaryAsync(runs.RunA);
        var runBSummary = await ReadSummaryAsync(runs.RunB);
        var adjustedRunA = runASummary with
        {
            CompletedAtUtc = runBSummary.CompletedAtUtc,
            DurationSeconds =
                (runBSummary.CompletedAtUtc - runASummary.StartedAtUtc)
                .TotalSeconds
        };
        await CaptureIO.WriteJsonAsync(
            Path.Combine(runs.RunA, "capture-summary.json"),
            adjustedRunA,
            CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunA, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "controlled-run-completed-at-not-after",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task ChangedPixelsRequireUnmaskedDiffAndExactHashBoundReview()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: true);
        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");

        var firstExit = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(3, firstExit);
        var evidence = JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(output),
            CaseInsensitiveJson)!;
        var difference = Assert.Single(evidence.PixelDifferences);
        Assert.True(difference.DifferingPixelCount > 0);
        Assert.True(File.Exists(Path.Combine(
            runs.RunA,
            difference.UnmaskedDiffPath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(evidence.Passed);

        var reviewPath = Path.Combine(runs.RunA, "screenshot-visual-review.json");
        await CaptureIO.WriteJsonAsync(
            reviewPath,
            new[]
            {
                new ScreenshotVisualReviewEntry(
                    difference.Key,
                    new string('0', 64),
                    difference.RunBSha256,
                    difference.UnmaskedDiffPath,
                    null,
                    null,
                    "independent-reviewer",
                    DateTimeOffset.UtcNow,
                    "accept",
                    "Stale hash fixture.")
            },
            CancellationToken.None);
        var staleExit = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            reviewPath,
            CancellationToken.None);
        Assert.Equal(1, staleExit);

        await CaptureIO.WriteJsonAsync(
            reviewPath,
            new[]
            {
                new ScreenshotVisualReviewEntry(
                    difference.Key,
                    difference.RunASha256,
                    difference.RunBSha256,
                    difference.UnmaskedDiffPath,
                    null,
                    null,
                    "independent-reviewer",
                    DateTimeOffset.UtcNow,
                    "accept",
                    "Fixture intentionally uses a different retained page image.")
            },
            CancellationToken.None);
        var acceptedExit = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            reviewPath,
            CancellationToken.None);

        Assert.Equal(0, acceptedExit);
        var accepted = JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(output),
            CaseInsensitiveJson)!;
        Assert.True(accepted.Passed);
        Assert.Equal("accepted", accepted.ReviewStatus);
    }

    [Theory]
    [InlineData("missing", "visual-review-json-invalid")]
    [InlineData("default", "visual-review-incomplete:home|mobile-320x568")]
    [InlineData("malformed", "visual-review-json-invalid")]
    [InlineData("before-run-b", "visual-review-incomplete:home|mobile-320x568")]
    [InlineData("future", "visual-review-incomplete:home|mobile-320x568")]
    [InlineData("non-utc", "visual-review-incomplete:home|mobile-320x568")]
    public async Task InvalidReviewedUtcIsRejectedDuringComparison(
        string timestampCase,
        string expectedReason)
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: true);
        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var initialExit = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);
        Assert.Equal(3, initialExit);
        var initialEvidence = await ReadEvidenceAsync(output);
        var difference = Assert.Single(initialEvidence.PixelDifferences);
        var review = new JsonObject
        {
            ["key"] = difference.Key,
            ["runASha256"] = difference.RunASha256,
            ["runBSha256"] = difference.RunBSha256,
            ["unmaskedDiffPath"] = difference.UnmaskedDiffPath,
            ["maskVersion"] = null,
            ["rectangles"] = null,
            ["reviewer"] = "independent-reviewer",
            ["decision"] = "accept",
            ["rationale"] = "Fixture intentionally uses a different retained page image."
        };
        SetReviewedUtc(
            review,
            timestampCase,
            initialEvidence.RunBCompletedAtUtc);
        var reviewPath = Path.Combine(runs.RunA, "screenshot-visual-review.json");
        await File.WriteAllTextAsync(
            reviewPath,
            new JsonArray(review).ToJsonString());

        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            reviewPath,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Equal("invalid", evidence.ReviewStatus);
        Assert.Contains(expectedReason, evidence.MetricDifferences);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReviewedUtcAtOrAfterRunBCompletionIsAcceptedDuringComparison(
        int secondsAfterCompletion)
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: true);
        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var initialExit = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);
        Assert.Equal(3, initialExit);
        var initialEvidence = await ReadEvidenceAsync(output);
        var difference = Assert.Single(initialEvidence.PixelDifferences);
        var reviewPath = Path.Combine(runs.RunA, "screenshot-visual-review.json");
        await CaptureIO.WriteJsonAsync(
            reviewPath,
            new[]
            {
                new ScreenshotVisualReviewEntry(
                    difference.Key,
                    difference.RunASha256,
                    difference.RunBSha256,
                    difference.UnmaskedDiffPath,
                    null,
                    null,
                    "independent-reviewer",
                    initialEvidence.RunBCompletedAtUtc.AddSeconds(secondsAfterCompletion),
                    "accept",
                    "Fixture intentionally uses a different retained page image.")
            },
            CancellationToken.None);

        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            reviewPath,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.True(evidence.Passed);
        Assert.Equal("accepted", evidence.ReviewStatus);
    }

    [Fact]
    public async Task MutatedRunBPngWithStaleJsonHashFailsBeforeChangedKeySelection()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var target = Path.Combine(runs.RunB, "screenshots", "home-mobile-320x568.png");
        var replacement = Path.Combine(
            BaselineTestFixture.FindRepositoryRoot(),
            "evidence",
            "baseline",
            "screenshots",
            "content-page-mobile-320x568.png");
        await File.WriteAllBytesAsync(target, await File.ReadAllBytesAsync(replacement));

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(output),
            CaseInsensitiveJson)!;
        Assert.Contains(
            evidence.MetricDifferences,
            reason => reason.Contains(
                "run-b:home|mobile-320x568:screenshot-hash-mismatch",
                StringComparison.Ordinal));
        Assert.Empty(evidence.PixelDifferences);
    }

    [Fact]
    public async Task MatchingForgedBrowserHashesInBothRunsAreRejected()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        foreach (var run in new[] { runs.RunA, runs.RunB })
        {
            var path = Path.Combine(
                run,
                "screenshot-capture-provenance.json");
            var provenance = JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            await CaptureIO.WriteJsonAsync(
                path,
                provenance with
                {
                    ExpectedExecutableSha256 = new string('0', 64),
                    ActualExecutableSha256 = new string('0', 64)
                },
                CancellationToken.None);
        }

        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            Path.Combine(runs.RunA, "screenshot-determinism.json"),
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(
                Path.Combine(runs.RunA, "screenshot-determinism.json")),
            CaseInsensitiveJson)!;
        Assert.Contains("browser-provenance-mismatch", evidence.MetricDifferences);
    }

    [Fact]
    public async Task ForgedRunBRepresentativeUrlIsRejected()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var path = Path.Combine(runs.RunB, "screenshots.json");
        var screenshots = JsonSerializer.Deserialize<ScreenshotRecord[]>(
            await File.ReadAllTextAsync(path),
            CaseInsensitiveJson)!;
        screenshots[0] = screenshots[0] with
        {
            Url = "https://www.husaynia.org/not-approved/"
        };
        await CaptureIO.WriteJsonAsync(path, screenshots, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(output),
            CaseInsensitiveJson)!;
        Assert.Contains(
            evidence.MetricDifferences,
            reason => reason.Contains(
                "run-b:",
                StringComparison.Ordinal)
                && reason.EndsWith(
                    ":representative-tuple-mismatch",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingRunBDecisionLedgerFailsBeforeDeterministicPass()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        File.Delete(Path.Combine(runs.RunB, "screenshot-network-decisions.json"));

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "run-b:required-artifact-missing:screenshot-network-decisions.json",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task MalformedRunBDecisionLedgerFailsBeforeDeterministicPass()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        await File.WriteAllTextAsync(
            Path.Combine(runs.RunB, "screenshot-network-decisions.json"),
            "{");

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            evidence.MetricDifferences,
            reason => reason.StartsWith(
                "run-b:evidence-parse-failed:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrphanedRunBDecisionLedgerFailsBeforeDeterministicPass()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var path = Path.Combine(runs.RunB, "screenshot-network-decisions.json");
        var decisions = await ReadDecisionsAsync(path);
        var request = decisions.First(item => item.EventType == "request");
        var terminal = decisions.First(item =>
            item.RequestId == request.RequestId
            && item.EventType == "response");
        decisions.Add(request with
        {
            CaptureKey = "orphan|mobile-320x568",
            RequestId = "orphan:attempt:1:request:1"
        });
        decisions.Add(terminal with
        {
            CaptureKey = "orphan|mobile-320x568",
            RequestId = "orphan:attempt:1:request:1"
        });
        await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunB, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            evidence.MetricDifferences,
            reason => reason.StartsWith(
                "run-b:network-decision-outside-capture-attempt:orphan|mobile-320x568:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncompleteRunBDecisionLedgerFailsBeforeDeterministicPass()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var path = Path.Combine(runs.RunB, "screenshot-network-decisions.json");
        var decisions = await ReadDecisionsAsync(path);
        var request = decisions.First(item => item.EventType == "request");
        decisions.RemoveAll(item =>
            item.RequestId == request.RequestId
            && item.EventType == "response");
        await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunB, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            $"run-b:network-terminal-count:{request.CaptureKey}:1:{request.RequestId}",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task HiddenRunBAttemptLedgerFailsBeforeDeterministicPass()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var path = Path.Combine(runs.RunB, "screenshot-network-decisions.json");
        var decisions = await ReadDecisionsAsync(path);
        var firstKey = decisions[0].CaptureKey;
        decisions.AddRange(decisions
            .Where(item => item.CaptureKey == firstKey)
            .Select(item => item with
            {
                Attempt = 2,
                RequestId = item.RequestId.Replace(
                    ":attempt:1:",
                    ":attempt:2:",
                    StringComparison.Ordinal)
            })
            .ToArray());
        await CaptureIO.WriteJsonAsync(path, decisions, CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(runs.RunB, CancellationToken.None);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            $"run-b:network-attempt-history-mismatch:{firstKey}",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task StaleRunBDecisionLedgerChecksumFailsBeforeDeterministicPass()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        var path = Path.Combine(runs.RunB, "screenshot-network-decisions.json");
        await File.AppendAllTextAsync(path, Environment.NewLine);

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            "run-b:comparison-checksum-mismatch:screenshot-network-decisions.json",
            evidence.MetricDifferences);
    }

    [Fact]
    public async Task MatchingForgedQualityClaimsAreRecomputedForBothRuns()
    {
        using var runs = await CreateRunsAsync(changeOnePixelImage: false);
        foreach (var run in new[] { runs.RunA, runs.RunB })
        {
            var path = Path.Combine(run, "screenshots.json");
            var screenshots = JsonSerializer.Deserialize<ScreenshotRecord[]>(
                await File.ReadAllTextAsync(path),
                CaseInsensitiveJson)!;
            screenshots[0] = screenshots[0] with
            {
                HorizontalOverflow = true,
                DocumentScrollWidth = screenshots[0].ActualViewportWidth + 1
            };
            await CaptureIO.WriteJsonAsync(path, screenshots, CancellationToken.None);
            await CaptureIO.WriteChecksumsAsync(run, CancellationToken.None);
        }

        var output = Path.Combine(runs.RunA, "screenshot-determinism.json");
        var exitCode = await ScreenshotDeterminismComparer.CompareAsync(
            runs.RunA,
            runs.RunB,
            output,
            null,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        var evidence = await ReadEvidenceAsync(output);
        Assert.Contains(
            evidence.MetricDifferences,
            reason => reason.StartsWith(
                "run-b:screenshot-quality-claim-mismatch:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void AggregatePngLimitFailsBeforeMultiGigabyteRetention()
    {
        Assert.True(ScreenshotDeterminismComparer.EnsureAggregatePngBytesWithinLimit(
            [1024, 2048],
            out var acceptedTotal));
        Assert.Equal(3072, acceptedTotal);

        Assert.False(ScreenshotDeterminismComparer.EnsureAggregatePngBytesWithinLimit(
            [
                ScreenshotDeterminismComparer.MaximumComparisonPngBytesPerRun,
                1
            ],
            out var rejectedTotal));
        Assert.True(
            rejectedTotal > ScreenshotDeterminismComparer.MaximumComparisonPngBytesPerRun);
    }

    private static async Task<RunPair> CreateRunsAsync(bool changeOnePixelImage)
    {
        var parent = Path.Combine(Path.GetTempPath(), $"husaynia-compare-{Guid.NewGuid():N}");
        var runA = Path.Combine(parent, "run-a");
        var runB = Path.Combine(parent, "run-b");
        Directory.CreateDirectory(Path.Combine(runA, "screenshots"));
        Directory.CreateDirectory(Path.Combine(runB, "screenshots"));
        var baselineScreenshots = Path.Combine(
            BaselineTestFixture.FindRepositoryRoot(),
            "evidence",
            "baseline",
            "screenshots");
        var rowsA = new List<ScreenshotRecord>();
        var rowsB = new List<ScreenshotRecord>();
        foreach (var representative in CaptureProfile.Approved.Representatives)
        {
            foreach (var viewport in CaptureProfile.Approved.Viewports)
            {
                var relative = $"screenshots/{representative.Key}-{viewport.Name}.png";
                var pngA = await File.ReadAllBytesAsync(
                    Path.Combine(
                        baselineScreenshots,
                        $"{representative.Key}-{viewport.Name}.png"));
                var changed = changeOnePixelImage
                    && representative.Key == "home"
                    && viewport.Name == "mobile-320x568";
                var pngB = changed
                    ? AddTextChunk(await File.ReadAllBytesAsync(
                        Path.Combine(
                            baselineScreenshots,
                            $"content-page-{viewport.Name}.png")))
                    : pngA;
                await File.WriteAllBytesAsync(
                    Path.Combine(runA, relative.Replace('/', Path.DirectorySeparatorChar)),
                    pngA);
                await File.WriteAllBytesAsync(
                    Path.Combine(runB, relative.Replace('/', Path.DirectorySeparatorChar)),
                    pngB);
                rowsA.Add(Row(representative.Key, representative.Value, viewport, relative, pngA));
                rowsB.Add(Row(
                    representative.Key,
                    representative.Value,
                    viewport,
                    relative,
                    pngB));
            }
        }

        await WriteRunAsync(
            runA,
            "run-a",
            new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.Zero),
            rowsA);
        await WriteRunAsync(
            runB,
            "run-b",
            new DateTimeOffset(2026, 8, 15, 20, 1, 0, TimeSpan.Zero),
            rowsB);
        return new RunPair(parent, runA, runB);
    }

    private static ScreenshotRecord Row(
        string template,
        Uri uri,
        CaptureViewport viewport,
        string relative,
        byte[] png)
    {
        var inspection = PngArtifactInspector.Inspect(png);
        var landmarks = new List<string> { "content", "heading", "menu" };
        if (template is "contact-form" or "donation-form")
        {
            landmarks.Add("form-shell");
        }

        if (template == "donation-form")
        {
            landmarks.Add("embedded-form");
        }

        return new ScreenshotRecord(
            template,
            uri.AbsoluteUri,
            viewport.Name,
            viewport.Width,
            viewport.Height,
            relative,
            inspection.Sha256,
            "captured",
            null,
            true,
            500,
            false,
            template is "contact-form" or "donation-form" ? 1 : 0,
            inspection.Length,
            inspection.Width,
            inspection.Height,
            template,
            "pass",
            inspection.ByteEntropy,
            false,
            false,
            landmarks.Order(StringComparer.Ordinal).ToArray(),
            viewport.Width,
            viewport.Height,
            viewport.Width,
            viewport.Height,
            1,
            viewport.Width,
            viewport.Height * 2,
            viewport.Width,
            viewport.Height * 2,
            true,
            0,
            0,
            [],
            [],
            false,
            "screenshot-network-decisions.json",
            "screenshot-capture-provenance.json",
            [],
            1,
            [],
            null);
    }

    private static ScreenshotCaptureProvenance Provenance(
        string captureId,
        DateTimeOffset capturedAt,
        IReadOnlyList<ScreenshotRecord> screenshots)
    {
        var identity = CaptureProfile.Approved.GetCurrentBrowserIdentity();
        var pinnedAnswers = CaptureProfile.Approved.StaticResources
            .Select(rule => rule.Host)
            .Append(CaptureProfile.Approved.PrimaryOrigin.Host)
            .ToDictionary(
                host => host,
                _ => (IReadOnlyList<string>)["93.184.216.34"],
                StringComparer.OrdinalIgnoreCase);
        return new ScreenshotCaptureProvenance(
            captureId,
            capturedAt,
            "Windows",
            "X64",
            "10.0.400",
            CaptureProfile.PlaywrightVersion,
            CaptureProfile.ChromiumVersion,
            PlaywrightScreenshotCapture.BrowserExecutableIdentity,
            "playwright.ps1 install --no-shell chromium",
            "cache",
            CaptureProfile.Approved.PolicyVersion,
            3,
            10,
            30,
            45,
            3000,
            true,
            CaptureProfile.ChromiumRevision,
            identity.ExecutableSha256,
            identity.ExecutableSha256,
            true,
            BaselineEvidenceValidator.ApprovedChildEnvironmentKeys
                .Order(StringComparer.Ordinal)
                .ToArray(),
            PlaywrightScreenshotCapture.BrowserWorkingDirectoryAttestation,
            false,
            CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(UriPartial.Authority),
            pinnedAnswers,
            pinnedAnswers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"MAP {pair.Key} {pair.Value[0]}")
                .Append("MAP * ~NOTFOUND")
                .ToArray(),
            false,
            false,
            false,
            false,
            false)
        {
            BrowserExecutableOutsideWorkspace = true,
            BrowserWorkingDirectoryOutsideWorkspace = true,
            BrowserWorkingDirectoryEmpty = true,
            ChildEnvironmentSanitized = true,
            BrowserLaunches =
            [
                new BrowserLaunchEvidence(
                    "fixture-browser",
                    capturedAt.AddSeconds(-1),
                    TrustedEndpointPolicy.ComputeResolverMapSha256(
                        pinnedAnswers
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => $"MAP {pair.Key} {pair.Value[0]}")
                            .Append("MAP * ~NOTFOUND")
                            .ToArray()),
                    null,
                    "bootstrap")
            ],
            DnsContextEpochs = screenshots
                .Select((screenshot, index) => new DnsContextEpochEvidence(
                    index + 1,
                    $"{screenshot.TemplateKey}|{screenshot.Viewport}",
                    1,
                    "fixture-browser",
                    $"fixture-context-{index + 1}",
                    capturedAt,
                    capturedAt,
                    capturedAt,
                    capturedAt,
                    pinnedAnswers
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new DnsPinBindingEvidence(
                            pair.Key,
                            pair.Key == CaptureProfile.Approved.PrimaryOrigin.Host
                                ? nameof(TrustedHostClass.PrimaryOrigin)
                                : nameof(TrustedHostClass.StaticResource),
                            pair.Value[0],
                            pair.Value,
                            CaptureIO.Sha256(Encoding.UTF8.GetBytes(pair.Value[0])),
                            capturedAt,
                            false))
                        .ToArray()))
                .ToArray()
        };
    }

    private static async Task WriteRunAsync(
        string run,
        string captureId,
        DateTimeOffset capturedAt,
        List<ScreenshotRecord> screenshots)
    {
        var provenance = Provenance(captureId, capturedAt, screenshots);
        var decisions = screenshots
            .SelectMany((screenshot, index) =>
            {
                var key = $"{screenshot.TemplateKey}|{screenshot.Viewport}";
                var requestId = $"{key}:attempt:1:request:1";
                var request = new ScreenshotNetworkDecision(
                    key,
                    requestId,
                    capturedAt,
                    "request",
                    "GET",
                    screenshot.Url,
                    "document",
                    true,
                    "allow",
                    "primary-origin-get-head",
                    null,
                    null,
                    CaptureProfile.Approved.PolicyVersion,
                    1,
                    true,
                    "93.184.216.34")
                {
                    DnsEpoch = index + 1,
                    BrowserInstanceId = "fixture-browser",
                    BrowserContextId = $"fixture-context-{index + 1}"
                };
                return new[]
                {
                    request,
                    request with
                    {
                        EventType = "response",
                        ReasonCode = "response-complete",
                        ResponseStatus = 200
                    }
                };
            })
            .ToArray();
        await CaptureIO.WriteJsonAsync(
            Path.Combine(run, "capture-summary.json"),
            new CaptureSummary(
                captureId,
                capturedAt.AddSeconds(-1),
                capturedAt.AddSeconds(1),
                CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri,
                true,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                screenshots.Count,
                screenshots.Count(item => item.Status == "captured"),
                screenshots.Count(item => item.QualityStatus == "pass"),
                0,
                2,
                decisions.Count(item => item.EventType == "request"),
                0),
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(run, "screenshots.json"),
            screenshots,
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(run, "screenshot-network-decisions.json"),
            decisions,
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(run, "screenshot-capture-provenance.json"),
            provenance,
            CancellationToken.None);
        await CaptureIO.WriteJsonAsync(
            Path.Combine(run, "screenshot-network-policy.json"),
            new
            {
                policyVersion = CaptureProfile.Approved.PolicyVersion,
                allowedMethods = ScreenshotNetworkPolicy.AllowedMethods,
                allowedOrigins = new[]
                {
                    CaptureProfile.Approved.PrimaryOrigin.GetLeftPart(
                        UriPartial.Authority)
                },
                allowedStaticHosts = ScreenshotNetworkPolicy.StaticHosts
                    .Order()
                    .ToArray(),
                allowedStaticResourceTypes =
                    ScreenshotNetworkPolicy.AllowedStaticResourceTypes,
                blockedCapabilities = ScreenshotNetworkPolicy.BlockedCapabilities,
                provenance.PinnedDnsAnswers,
                provenance.ChromiumHostResolverRules,
                navigationAttempts =
                    PlaywrightScreenshotCapture.MaximumNavigationAttempts,
                navigationAttemptTimeoutSeconds =
                    PlaywrightScreenshotCapture.NavigationAttemptTimeoutMilliseconds
                    / 1000,
                readinessTimeoutSeconds =
                    PlaywrightScreenshotCapture.MaximumReadinessDuration.TotalSeconds,
                captureTimeoutSeconds =
                    PlaywrightScreenshotCapture.MaximumCaptureDuration.TotalSeconds,
                interCaptureDelayMilliseconds =
                    PlaywrightScreenshotCapture.InterCaptureDelayMilliseconds,
                enforcement = "comparison fixture"
            },
            CancellationToken.None);
        await CaptureIO.WriteChecksumsAsync(run, CancellationToken.None);
    }

    private static async Task<List<ScreenshotNetworkDecision>> ReadDecisionsAsync(
        string path) =>
        JsonSerializer.Deserialize<List<ScreenshotNetworkDecision>>(
            await File.ReadAllTextAsync(path),
            CaseInsensitiveJson)!;

    private static async Task<ScreenshotDeterminismEvidence> ReadEvidenceAsync(
        string path) =>
        JsonSerializer.Deserialize<ScreenshotDeterminismEvidence>(
            await File.ReadAllTextAsync(path),
            CaseInsensitiveJson)!;

    private static void SetReviewedUtc(
        JsonObject review,
        string timestampCase,
        DateTimeOffset runBCompletedAtUtc)
    {
        switch (timestampCase)
        {
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

    private static string ValidStrictReviewJson() =>
        """
        [{"key":"home|desktop","runASha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","runBSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","unmaskedDiffPath":"screenshot-diffs/home.png","maskVersion":null,"rectangles":null,"reviewer":"reviewer","reviewedUtc":"2026-08-18T20:00:00Z","decision":"accept","rationale":"reviewed unmasked difference"}]
        """;

    private static async Task AssertStrictReviewRejectedAsync(string json)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-visual-review-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, json);
            await Assert.ThrowsAsync<JsonException>(() =>
                ScreenshotVisualReviewParser.ParseAsync(
                    path,
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<CaptureSummary> ReadSummaryAsync(string run) =>
        JsonSerializer.Deserialize<CaptureSummary>(
            await File.ReadAllTextAsync(Path.Combine(run, "capture-summary.json")),
            CaseInsensitiveJson)!;

    private static async Task<ScreenshotCaptureProvenance> ReadProvenanceAsync(
        string run) =>
        JsonSerializer.Deserialize<ScreenshotCaptureProvenance>(
            await File.ReadAllTextAsync(
                Path.Combine(run, "screenshot-capture-provenance.json")),
            CaseInsensitiveJson)!;

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.Copy(
                file,
                Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    private static byte[] AddTextChunk(byte[] png)
    {
        var payload = Encoding.ASCII.GetBytes("fixture\0changed-pixels");
        var type = Encoding.ASCII.GetBytes("tEXt");
        var chunk = new byte[12 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
        type.CopyTo(chunk, 4);
        payload.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(
            chunk.AsSpan(8 + payload.Length, 4),
            Crc32(chunk.AsSpan(4, 4 + payload.Length)));
        var result = new byte[png.Length + chunk.Length];
        var iendOffset = png.Length - 12;
        png.AsSpan(0, iendOffset).CopyTo(result);
        chunk.CopyTo(result, iendOffset);
        png.AsSpan(iendOffset).CopyTo(result.AsSpan(iendOffset + chunk.Length));
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0
                    ? crc >> 1
                    : crc >> 1 ^ 0xedb88320u;
            }
        }

        return ~crc;
    }

    private sealed class RunPair(string parent, string runA, string runB) : IDisposable
    {
        public string RunA { get; } = runA;
        public string RunB { get; } = runB;

        public void Dispose()
        {
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }
}
