namespace Husaynia.BaselineCapture;

public static class Program
{
    internal const int DefaultMaxDurationMinutes = 60;
    internal const int MinimumMaxDurationMinutes = 1;
    internal const int MaximumMaxDurationMinutes = 90;

    public static Task<int> Main(string[] args) =>
        ExecuteWithFailureHandlingAsync(
            () => DispatchAsync(args),
            Console.Error);

    internal static async Task<int> ExecuteWithFailureHandlingAsync(
        Func<Task<int>> command,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return await command();
        }
        catch (CaptureSafetyException ex)
        {
            WriteLineBestEffort(
                error,
                $"Safety refusal: {ex.ReasonCode}");
            return 2;
        }
        catch (UriFormatException)
        {
            WriteLineBestEffort(
                error,
                "Safety refusal: invalid URI.");
            return 2;
        }
        catch (Exception ex)
        {
            WriteLineBestEffort(
                error,
                $"command-failed:{ex.GetType().Name}");
            return 1;
        }
    }

    private static void WriteLineBestEffort(
        TextWriter writer,
        string message)
    {
        try
        {
            writer.WriteLine(message);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            WriteUsage();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "capture" => await CaptureAsync(args),
            "recapture-screenshots" => await RecaptureAsync(args),
            "compare-screenshot-metrics" => await CompareAsync(args),
            "verify" => await VerifyAsync(args),
            "promote" => await PromoteAsync(args),
            _ => UsageFailure("Unsupported command.")
        };
    }

    public static bool HasCompleteScreenshotSet(CaptureSummary summary) =>
        summary.Status == CaptureFailureEvidence.CompleteStatus
        && summary.FailureReason is null
        && summary.FailureStage is null
        && summary.ScreenshotCount == 36
        && summary.SuccessfulScreenshotCount == 36
        && summary.QualityPassScreenshotCount == 36;

    private static async Task<int> CaptureAsync(string[] args)
    {
        if (!args.Contains("--no-submit", StringComparer.OrdinalIgnoreCase))
        {
            return UsageFailure("Safety refusal: capture requires --no-submit.");
        }

        var baseUri = ParseApprovedBaseUri(GetValue(args, "--base-url"));
        var output = GetValue(args, "--output")
            ?? Path.GetFullPath(Path.Combine(
                Environment.CurrentDirectory,
                "evidence",
                "baseline-runs",
                DateTimeOffset.UtcNow.ToString(
                    "yyyyMMddTHHmmssZ",
                    System.Globalization.CultureInfo.InvariantCulture)));
        var options = CreateCaptureOptions(args, baseUri, output);
        using var service = new BaselineCaptureService(options);
        var summary = await service.CaptureAsync(CancellationToken.None);
        Console.WriteLine($"Capture {summary.CaptureId} complete.");
        Console.WriteLine(
            $"Routes: {summary.SuccessfulRouteCount}/{summary.RouteCount}; "
            + $"assets: {summary.DownloadedAssetCount}/{summary.AssetCount}; "
            + $"screenshots captured: {summary.SuccessfulScreenshotCount}/{summary.ScreenshotCount}; "
            + $"quality-pass: {summary.QualityPassScreenshotCount}/{summary.ScreenshotCount}; "
            + $"duration: {summary.DurationSeconds:F1}s; risks: {summary.ResidualRiskCount}");
        if (!HasCompleteScreenshotSet(summary))
        {
            Console.Error.WriteLine(
                "ADR-009 failure: promotion requires 36/36 captured and 36/36 quality-pass; "
                + $"actual {summary.SuccessfulScreenshotCount}/36 captured and "
                + $"{summary.QualityPassScreenshotCount}/36 quality-pass.");
            return 1;
        }

        return 0;
    }

    internal static CaptureOptions CreateCaptureOptions(
        string[] args,
        Uri baseUri,
        string output) =>
        new(
            baseUri,
            output,
            true,
            ReadBoundedOption(args, "--delay-ms", 100, 0, 60_000),
            ReadBoundedOption(args, "--timeout-seconds", 30, 1, 30),
            ReadBoundedOption(args, "--max-asset-bytes", 25 * 1024 * 1024, 1, 25 * 1024 * 1024),
            ReadBoundedOption(args, "--max-concurrency", 6, 1, 6),
            ReadBoundedOption(
                args,
                "--max-duration-minutes",
                DefaultMaxDurationMinutes,
                MinimumMaxDurationMinutes,
                MaximumMaxDurationMinutes));

    private static async Task<int> RecaptureAsync(string[] args)
    {
        if (!args.Contains("--no-submit", StringComparer.OrdinalIgnoreCase))
        {
            return UsageFailure("Safety refusal: screenshot recapture requires --no-submit.");
        }

        var evidence = GetValue(args, "--evidence");
        var output = GetValue(args, "--output");
        if (string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(output))
        {
            return UsageFailure("Screenshot recapture requires --evidence and --output.");
        }

        return await ScreenshotMatrixRunner.RunAsync(
            evidence,
            output,
            CancellationToken.None);
    }

    private static async Task<int> CompareAsync(string[] args)
    {
        var firstScreenshots = GetValue(args, "--first-screenshots");
        var firstProvenance = GetValue(args, "--first-provenance");
        var secondEvidence = GetValue(args, "--second-evidence");
        var comparisonOutput = GetValue(args, "--output");
        var review = GetValue(args, "--review");
        if (new[] { firstScreenshots, firstProvenance, secondEvidence, comparisonOutput }
            .Any(string.IsNullOrWhiteSpace))
        {
            return UsageFailure(
                "Comparison requires --first-screenshots, --first-provenance, --second-evidence, and --output.");
        }

        var runA = Path.GetDirectoryName(Path.GetFullPath(firstScreenshots!));
        if (runA is null
            || !Path.GetFullPath(firstProvenance!).Equals(
                Path.Combine(runA, "screenshot-capture-provenance.json"),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(firstScreenshots!).Equals(
                Path.Combine(runA, "screenshots.json"),
                StringComparison.OrdinalIgnoreCase))
        {
            return UsageFailure("Run A screenshot and provenance paths must identify one evidence directory.");
        }

        var canonicalOutput = Path.Combine(runA, "screenshot-determinism.json");
        if (!Path.GetFullPath(comparisonOutput!).Equals(
                canonicalOutput,
                StringComparison.OrdinalIgnoreCase)
            || review is not null
                && !Path.GetFullPath(review).Equals(
                    Path.Combine(runA, "screenshot-visual-review.json"),
                    StringComparison.OrdinalIgnoreCase))
        {
            return UsageFailure(
                "Comparison output and optional review must be the canonical files beneath Run A.");
        }

        return await ScreenshotDeterminismComparer.CompareAsync(
            runA,
            secondEvidence!,
            canonicalOutput,
            review,
            CancellationToken.None);
    }

    private static async Task<int> VerifyAsync(string[] args)
    {
        var evidence = GetValue(args, "--evidence");
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return UsageFailure("Verification requires --evidence.");
        }

        var validation = await new BaselineEvidenceValidator().FinalizeAsync(
            evidence,
            CancellationToken.None);
        if (!validation.Passed)
        {
            Console.Error.WriteLine(
                $"verification-failed:{string.Join(',', validation.ReasonCodes)}");
            return 1;
        }

        Console.WriteLine($"verification-passed files={validation.Files.Count}");
        return 0;
    }

    private static async Task<int> PromoteAsync(string[] args)
    {
        if (!args.Contains("--approved", StringComparer.OrdinalIgnoreCase))
        {
            return UsageFailure("Promotion refusal: an explicit --approved flag is required.");
        }

        var from = GetValue(args, "--from");
        var to = GetValue(args, "--to");
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return UsageFailure("Promotion requires --from and --to.");
        }

        var canonicalDestination =
            CanonicalizeBaselineDestination(to);
        if (canonicalDestination is null)
        {
            return UsageFailure(
                "Promotion refusal: --to must be the canonical repository evidence/baseline path.");
        }

        return await new BaselinePromotionService().PromoteAsync(
            from,
            canonicalDestination,
            CancellationToken.None);
    }

    internal static bool IsCanonicalBaselineDestination(string destination)
        => CanonicalizeBaselineDestination(destination) is not null;

    internal static string? CanonicalizeBaselineDestination(
        string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var expected = Path.GetFullPath(Path.Combine(
            FindRepositoryRoot(),
            "evidence",
            "baseline"));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(destination).Equals(
            expected,
            comparison)
            ? expected
            : null;
    }

    private static Uri ParseApprovedBaseUri(string? value)
    {
        var uri = new Uri(value ?? CaptureProfile.Approved.PrimaryOrigin.AbsoluteUri);
        if (!CaptureProfile.Approved.IsApprovedOrigin(uri))
        {
            throw new CaptureSafetyException("base-url-not-approved");
        }

        return CaptureProfile.Approved.PrimaryOrigin;
    }

    private static int UsageFailure(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string? GetValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int ReadBoundedOption(
        string[] args,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = GetValue(args, name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || value < minimum
            || value > maximum)
        {
            throw new CaptureSafetyException(
                $"capture-option-out-of-range:{name}:accepted-range={minimum}..{maximum}");
        }

        return value;
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(
                        directory.FullName,
                        "tools",
                        "Husaynia.BaselineCapture"))
                    && Directory.Exists(Path.Combine(directory.FullName, "evidence")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new CaptureSafetyException("repository-root-not-found");
    }

    private static void WriteUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine(
            "  capture --no-submit --output RUN_A [--base-url https://www.husaynia.org/]");
        Console.WriteLine(
            "    [--max-duration-minutes MINUTES (accepted 1..90; default 60)]");
        Console.WriteLine("  recapture-screenshots --no-submit --evidence RUN_A --output RUN_B");
        Console.WriteLine("  compare-screenshot-metrics --first-screenshots RUN_A/screenshots.json");
        Console.WriteLine("    --first-provenance RUN_A/screenshot-capture-provenance.json");
        Console.WriteLine("    --second-evidence RUN_B --output RUN_A/screenshot-determinism.json");
        Console.WriteLine("    [--review RUN_A/screenshot-visual-review.json]");
        Console.WriteLine("  verify --evidence RUN_A");
        Console.WriteLine("  promote --approved --from RUN_A --to evidence/baseline");
    }
}
