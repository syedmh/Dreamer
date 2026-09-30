using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Husaynia.BaselineCapture;

public enum PromotionFaultPoint
{
    BeforeCopy,
    AfterCopyBeforeVerify,
    AfterCopyVerify,
    AfterBackupMove,
    AfterSwap,
    AfterPostVerify,
    BeforeRestore,
    AfterRestore
}

public readonly record struct PromotionArtifactIdentity(
    uint VolumeSerialNumber,
    ulong FileId);

public sealed record PromotionTreeIdentityEntry(
    string RelativePath,
    bool IsDirectory,
    PromotionArtifactIdentity Identity,
    uint LinkCount);

public sealed record PromotionTreeSnapshot(
    PromotionArtifactIdentity RootIdentity,
    IReadOnlyList<BaselineFileManifestEntry> Files,
    IReadOnlyList<PromotionTreeIdentityEntry> Entries);

public sealed class PromotionFileSystemSafetyException(string message)
    : IOException(message);

public sealed class PromotionLockUnavailableException(string message)
    : IOException(message);

public interface IPromotionFileSystem
{
    IPromotionFileSystemLease OpenLease(
        string sourceRoot,
        string destinationRoot);

    IReadOnlyList<BaselineFileManifestEntry> EnumerateTree(string root);
}

public interface IPromotionFileSystemLease : IDisposable
{
    string SourceRoot { get; }

    string DestinationRoot { get; }

    string DestinationName { get; }

    IReadOnlyList<string> ListPromotionResidue();

    PromotionTreeSnapshot CaptureSourceTree();

    PromotionTreeSnapshot? CaptureDestinationTree();

    PromotionTreeSnapshot CaptureSiblingTree(string siblingName);

    PromotionArtifactIdentity? TryGetSiblingIdentity(string siblingName);

    Task<PromotionTreeSnapshot> CopySourceToSiblingCreateNewAsync(
        string siblingName,
        CancellationToken cancellationToken);

    PromotionArtifactIdentity CreateDiagnosticFile(
        string siblingName,
        ReadOnlySpan<byte> content);

    void MoveSibling(
        string sourceSiblingName,
        string destinationSiblingName,
        PromotionArtifactIdentity expectedSourceIdentity);

    void DeleteTreeIfExact(
        string siblingName,
        PromotionTreeSnapshot expectedSnapshot);

    void DeleteOwnedTreeIfRootIdentity(
        string siblingName,
        PromotionArtifactIdentity expectedRootIdentity);

    void DeleteFileIfIdentity(
        string siblingName,
        PromotionArtifactIdentity expectedIdentity);
}

internal sealed record PromotionDiagnosticJournal(
    string Schema,
    int Version,
    string TransactionId,
    string DestinationDirectoryName,
    string SourceDirectoryName,
    string CandidateDirectoryName,
    string PriorDirectoryName,
    string FailedDirectoryName,
    bool PriorExisted,
    string Phase,
    IReadOnlyList<BaselineFileManifestEntry> SourceManifest,
    IReadOnlyList<BaselineFileManifestEntry> PriorManifest);

internal static class PromotionDiagnosticJournalStore
{
    private const int MaximumJournalBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions =
        CreateJsonOptions();

    public static byte[] Serialize(PromotionDiagnosticJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            JsonOptions);
        if (bytes.Length is <= 0 or > MaximumJournalBytes)
        {
            throw new InvalidDataException(
                "promotion-journal-size-invalid");
        }

        return bytes;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}

internal sealed class PromotionTransactionContext(
    string token,
    string destinationName,
    string candidateName,
    string priorName,
    string failedName,
    string journalName,
    PromotionTreeSnapshot sourceSnapshot,
    PromotionTreeSnapshot candidateSnapshot,
    PromotionTreeSnapshot? priorSnapshot,
    PromotionArtifactIdentity journalIdentity)
{
    public string Token { get; } = token;

    public string DestinationName { get; } = destinationName;

    public string CandidateName { get; } = candidateName;

    public string PriorName { get; } = priorName;

    public string FailedName { get; } = failedName;

    public string JournalName { get; } = journalName;

    public PromotionTreeSnapshot SourceSnapshot { get; } = sourceSnapshot;

    public PromotionTreeSnapshot CandidateSnapshot { get; } =
        candidateSnapshot;

    public PromotionTreeSnapshot? PriorSnapshot { get; } = priorSnapshot;

    public PromotionArtifactIdentity JournalIdentity { get; } =
        journalIdentity;

    public bool PriorMoved { get; set; }

    public bool PriorMoveAttempted { get; set; }

    public bool SwapAttempted { get; set; }

    public bool CommitBoundaryPassed { get; set; }
}

internal sealed record PromotionOutcome(
    string Phase,
    string Reason,
    int ExitCode,
    string DestinationName,
    IReadOnlyList<string> PriorNames,
    IReadOnlyList<string> CandidateNames,
    IReadOnlyList<string> FailedNames,
    IReadOnlyList<string> JournalNames,
    string? State = null);

internal sealed record PromotionExitDiagnostic(
    string Schema,
    string Phase,
    string Reason,
    int Exit,
    string Destination,
    IReadOnlyList<string> Priors,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Journals,
    string? State);

public sealed class BaselinePromotionService
{
    private readonly BaselineEvidenceValidator _validator;
    private readonly IPromotionFileSystem _fileSystem;
    private readonly Action<PromotionFaultPoint>? _faultInjector;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private static readonly JsonSerializerOptions ExitJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition =
                JsonIgnoreCondition.WhenWritingNull
        };

    public BaselinePromotionService(
        BaselineEvidenceValidator? validator = null,
        IPromotionFileSystem? fileSystem = null,
        IPromotionFileSystemTrustPolicy? trustPolicy = null,
        Action<PromotionFaultPoint>? faultInjector = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        _validator = validator ?? new BaselineEvidenceValidator();
        var effectiveTrustPolicy = trustPolicy
            ?? new PhysicalPromotionFileSystemTrustPolicy();
        _fileSystem = fileSystem
            ?? new PhysicalPromotionFileSystem(effectiveTrustPolicy);
        _faultInjector = faultInjector;
        _output = output ?? Console.Out;
        _error = error ?? Console.Error;
    }

    public async Task<int> PromoteAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var outcome = await PromoteCoreAsync(
            source,
            destination,
            cancellationToken);
        await WriteOutcomeBestEffortAsync(outcome);
        return outcome.ExitCode;
    }

    private async Task<PromotionOutcome> PromoteCoreAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        string sourceRoot;
        string destinationRoot;
        try
        {
            sourceRoot = Path.GetFullPath(source);
            destinationRoot = Path.GetFullPath(destination);
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return CreateOutcome(
                phase: "path-validation",
                reason: "promotion-path-invalid",
                exitCode: 2,
                destinationName: SafeLogicalName(destination));
        }

        var destinationName = SafeLogicalName(destinationRoot);
        if (sourceRoot.Equals(
                destinationRoot,
                StringComparison.OrdinalIgnoreCase)
            || !IsSafeSiblingName(destinationName))
        {
            return CreateOutcome(
                phase: "path-validation",
                reason:
                "promotion-same-volume-distinct-paths-required",
                exitCode: 2,
                destinationName);
        }

        IPromotionFileSystemLease lease;
        try
        {
            lease = _fileSystem.OpenLease(sourceRoot, destinationRoot);
        }
        catch (PromotionLockUnavailableException)
        {
            return CreateOutcome(
                phase: "lease-open",
                reason: "promotion-lock-held",
                exitCode: 2,
                destinationName);
        }
        catch (Exception ex) when (
            IsTrustRefusal(ex)
            || ex is InvalidDataException)
        {
            return CreateOutcome(
                phase: "lease-open",
                reason: StableFailureReason(ex),
                exitCode: 2,
                destinationName);
        }

        using (lease)
        {
            IReadOnlyList<string> residue;
            try
            {
                residue = lease.ListPromotionResidue();
            }
            catch (Exception ex) when (
                IsTrustRefusal(ex)
                || ex is InvalidDataException)
            {
                return CreateOutcome(
                    phase: "residue-scan",
                    reason: StableFailureReason(ex),
                    exitCode: 2,
                    lease.DestinationName);
            }

            if (residue.Count > 0)
            {
                return CreateResidueOutcome(
                    phase: "residue-refusal",
                    reason:
                    "promotion-interrupted-state-manual-recovery-required",
                    exitCode: 4,
                    lease.DestinationName,
                    residue,
                    state: "residue-preserved");
            }

            return await PromoteUnderLeaseAsync(
                lease,
                cancellationToken);
        }
    }

    private async Task<PromotionOutcome> PromoteUnderLeaseAsync(
        IPromotionFileSystemLease lease,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var candidateName =
            $"{lease.DestinationName}.candidate-{token}";
        var priorName = $"{lease.DestinationName}.prior-{token}";
        var failedName = $"{lease.DestinationName}.failed-{token}";
        var journalName =
            $"{lease.DestinationName}.transaction-{token}.json";
        PromotionTreeSnapshot? sourceSnapshot = null;
        PromotionTreeSnapshot? candidateSnapshot = null;
        PromotionTreeSnapshot? priorSnapshot = null;
        PromotionArtifactIdentity? journalIdentity = null;
        PromotionTransactionContext? context = null;
        var phase = "source-capture";
        try
        {
            sourceSnapshot = lease.CaptureSourceTree();
            phase = "source-validation";
            var sourceValidation = await _validator.ValidateReadOnlyAsync(
                lease.SourceRoot,
                cancellationToken);
            if (!sourceValidation.Passed)
            {
                return CreateOutcome(
                    phase: "source-validation",
                    reason: "promotion-source-validation-failed",
                    exitCode: 1,
                    lease.DestinationName);
            }

            EnsureFileManifestsEqual(
                sourceSnapshot.Files,
                sourceValidation.Files,
                "promotion-source-identity-changed");
            EnsureSnapshotsEqual(
                sourceSnapshot,
                lease.CaptureSourceTree(),
                requireIdentity: true,
                "promotion-source-identity-changed");

            _faultInjector?.Invoke(PromotionFaultPoint.BeforeCopy);
            phase = "candidate-copy";
            candidateSnapshot =
                await lease.CopySourceToSiblingCreateNewAsync(
                    candidateName,
                    cancellationToken);
            _faultInjector?.Invoke(
                PromotionFaultPoint.AfterCopyBeforeVerify);

            phase = "candidate-validation";
            var candidatePath = SiblingPath(
                lease.DestinationRoot,
                candidateName);
            var candidateValidation =
                await _validator.ValidateReadOnlyAsync(
                    candidatePath,
                    cancellationToken);
            var candidateAfterValidation =
                lease.CaptureSiblingTree(candidateName);
            if (!candidateValidation.Passed)
            {
                throw new InvalidDataException(
                    "promotion-copy-verification-failed");
            }

            EnsureFileManifestsEqual(
                sourceSnapshot.Files,
                candidateValidation.Files,
                "promotion-copy-verification-failed");
            EnsureFileManifestsEqual(
                sourceSnapshot.Files,
                candidateAfterValidation.Files,
                "promotion-copy-verification-failed");
            EnsureSnapshotsEqual(
                candidateSnapshot,
                candidateAfterValidation,
                requireIdentity: true,
                "promotion-candidate-identity-changed");
            EnsureSnapshotsEqual(
                sourceSnapshot,
                lease.CaptureSourceTree(),
                requireIdentity: true,
                "promotion-source-mutated");

            _faultInjector?.Invoke(PromotionFaultPoint.AfterCopyVerify);
            phase = "prior-capture";
            priorSnapshot = lease.CaptureDestinationTree();
            var journal = new PromotionDiagnosticJournal(
                Schema: "husaynia-promotion-journal-diagnostic",
                Version: 1,
                TransactionId: token,
                DestinationDirectoryName: lease.DestinationName,
                SourceDirectoryName: LogicalName(lease.SourceRoot),
                CandidateDirectoryName: candidateName,
                PriorDirectoryName: priorName,
                FailedDirectoryName: failedName,
                PriorExisted: priorSnapshot is not null,
                Phase: "prepared",
                SourceManifest: sourceSnapshot.Files,
                PriorManifest: priorSnapshot?.Files ?? []);
            phase = "journal-create";
            journalIdentity = lease.CreateDiagnosticFile(
                journalName,
                PromotionDiagnosticJournalStore.Serialize(journal));
            context = new PromotionTransactionContext(
                token,
                lease.DestinationName,
                candidateName,
                priorName,
                failedName,
                journalName,
                sourceSnapshot,
                candidateSnapshot,
                priorSnapshot,
                journalIdentity.Value);

            if (priorSnapshot is not null)
            {
                phase = "prior-move";
                EnsureSnapshotsEqual(
                    priorSnapshot,
                    lease.CaptureDestinationTree()
                        ?? throw new PromotionFileSystemSafetyException(
                            "promotion-prior-identity-changed"),
                    requireIdentity: true,
                    "promotion-prior-identity-changed");
                context.PriorMoveAttempted = true;
                lease.MoveSibling(
                    lease.DestinationName,
                    priorName,
                    priorSnapshot.RootIdentity);
                context.PriorMoved = true;
            }

            _faultInjector?.Invoke(
                PromotionFaultPoint.AfterBackupMove);
            phase = "candidate-move";
            EnsureSnapshotsEqual(
                candidateSnapshot,
                lease.CaptureSiblingTree(candidateName),
                requireIdentity: true,
                "promotion-candidate-identity-changed");
            context.SwapAttempted = true;
            lease.MoveSibling(
                candidateName,
                lease.DestinationName,
                candidateSnapshot.RootIdentity);

            _faultInjector?.Invoke(PromotionFaultPoint.AfterSwap);
            phase = "destination-validation";
            var destinationValidation =
                await _validator.ValidateReadOnlyAsync(
                    lease.DestinationRoot,
                    cancellationToken);
            var destinationSnapshot =
                lease.CaptureDestinationTree()
                ?? throw new InvalidDataException(
                    "promotion-post-swap-verification-failed");
            if (!destinationValidation.Passed)
            {
                throw new InvalidDataException(
                    "promotion-post-swap-verification-failed");
            }

            EnsureFileManifestsEqual(
                sourceSnapshot.Files,
                destinationValidation.Files,
                "promotion-post-swap-verification-failed");
            EnsureSnapshotsEqual(
                candidateSnapshot,
                destinationSnapshot,
                requireIdentity: true,
                "promotion-post-swap-verification-failed");

            _faultInjector?.Invoke(
                PromotionFaultPoint.AfterPostVerify);
            cancellationToken.ThrowIfCancellationRequested();
            context.CommitBoundaryPassed = true;

            phase = "commit-cleanup";
            try
            {
                if (priorSnapshot is not null)
                {
                    lease.DeleteTreeIfExact(
                        priorName,
                        priorSnapshot);
                }

                lease.DeleteFileIfIdentity(
                    journalName,
                    journalIdentity.Value);
            }
            catch (Exception)
            {
                return CreateOutcome(
                    phase: "commit-cleanup",
                    reason: "promotion-prior-cleanup-failed",
                    exitCode: 4,
                    context,
                    state: "verified-destination-preserved");
            }

            return CreateOutcome(
                phase: "complete",
                reason: "promotion-complete",
                exitCode: 0,
                context,
                state: "committed");
        }
        catch (Exception ex)
        {
            var failureReason = StableFailureReason(ex);
            if (context?.CommitBoundaryPassed == true)
            {
                return CreateOutcome(
                    phase,
                    reason: "promotion-post-commit-failure",
                    exitCode: 4,
                    context,
                    state: "verified-destination-preserved");
            }

            if (context is not null
                && (context.PriorMoveAttempted
                    || context.PriorMoved
                    || context.SwapAttempted))
            {
                return await RollbackSameProcessAsync(
                    lease,
                    context,
                    failureReason);
            }

            var cleanupSucceeded = TryCleanupPreMutation(
                lease,
                candidateName,
                candidateSnapshot,
                journalName,
                journalIdentity,
                out _);
            if (!cleanupSucceeded)
            {
                return CreateResidueOutcome(
                    phase,
                    reason: "promotion-rollback-cleanup-failed",
                    exitCode: 4,
                    lease.DestinationName,
                    PreMutationRecoveryNames(
                        candidateName,
                        candidateSnapshot,
                        journalName,
                        journalIdentity,
                        ex),
                    state: "residue-preserved");
            }

            if (ex is PromotionArtifactConstructionException
                constructionException)
            {
                return CreateResidueOutcome(
                    phase,
                    constructionException.Message,
                    exitCode: 4,
                    lease.DestinationName,
                    [constructionException.LogicalName],
                    state: "residue-preserved");
            }

            return CreateOutcome(
                phase,
                failureReason,
                exitCode: IsTrustRefusal(ex) ? 2 : 1,
                lease.DestinationName);
        }
    }

    private async Task<PromotionOutcome> RollbackSameProcessAsync(
        IPromotionFileSystemLease lease,
        PromotionTransactionContext context,
        string failureReason)
    {
        var rollbackPhase = "candidate-isolation";
        try
        {
            MoveCandidateAsideIfNecessary(lease, context);
            rollbackPhase = "before-restore";
            _faultInjector?.Invoke(PromotionFaultPoint.BeforeRestore);

            if (context.PriorSnapshot is not null)
            {
                rollbackPhase = "prior-restore";
                RestorePriorIfNecessary(lease, context);
                rollbackPhase = "prior-verification";
                var restored = lease.CaptureDestinationTree()
                    ?? throw new PromotionFileSystemSafetyException(
                        "promotion-restore-verification-failed");
                EnsureSnapshotsEqual(
                    context.PriorSnapshot,
                    restored,
                    requireIdentity: true,
                    "promotion-restore-verification-failed");
            }
            else if (lease.TryGetSiblingIdentity(
                         context.DestinationName)
                     is not null)
            {
                throw new PromotionFileSystemSafetyException(
                    "promotion-initial-rollback-destination-present");
            }
        }
        catch (Exception)
        {
            return CreateOutcome(
                phase: rollbackPhase,
                reason: "promotion-restore-failed",
                exitCode: 4,
                context,
                state: "recovery-required");
        }

        try
        {
            _faultInjector?.Invoke(PromotionFaultPoint.AfterRestore);
        }
        catch (Exception)
        {
            failureReason = "promotion-after-restore-fault";
        }

        try
        {
            CleanupRollbackArtifacts(lease, context);
        }
        catch (Exception)
        {
            return CreateOutcome(
                phase: "rollback-cleanup",
                reason: "promotion-rollback-cleanup-failed",
                exitCode: 4,
                context,
                state: "recovery-required");
        }

        return CreateOutcome(
            phase: "rollback-complete",
            reason: failureReason,
            exitCode: 1,
            context,
            state: "prior-restored");
    }

    private async Task WriteOutcomeBestEffortAsync(
        PromotionOutcome outcome)
    {
        try
        {
            var diagnostic = new PromotionExitDiagnostic(
                Schema: "husaynia-promotion-exit",
                Phase: outcome.Phase,
                Reason: outcome.Reason,
                Exit: outcome.ExitCode,
                Destination: outcome.DestinationName,
                Priors: outcome.PriorNames,
                Candidates: outcome.CandidateNames,
                Failed: outcome.FailedNames,
                Journals: outcome.JournalNames,
                State: outcome.State);
            var line = JsonSerializer.Serialize(
                diagnostic,
                ExitJsonOptions);
            var writer = outcome.ExitCode == 0
                ? _output
                : _error;
            await writer.WriteLineAsync(line);
        }
        catch (Exception)
        {
        }
    }

    private static PromotionOutcome CreateOutcome(
        string phase,
        string reason,
        int exitCode,
        string destinationName,
        IEnumerable<string>? priorNames = null,
        IEnumerable<string>? candidateNames = null,
        IEnumerable<string>? failedNames = null,
        IEnumerable<string>? journalNames = null,
        string? state = null) =>
        new(
            EscapeLogicalName(phase),
            EscapeLogicalName(reason),
            exitCode,
            EscapeLogicalName(destinationName),
            BoundEscapedNames(priorNames),
            BoundEscapedNames(candidateNames),
            BoundEscapedNames(failedNames),
            BoundEscapedNames(journalNames),
            state is null ? null : EscapeLogicalName(state));

    private static PromotionOutcome CreateOutcome(
        string phase,
        string reason,
        int exitCode,
        PromotionTransactionContext context,
        string? state = null) =>
        CreateOutcome(
            phase,
            reason,
            exitCode,
            context.DestinationName,
            priorNames: [context.PriorName],
            candidateNames: [context.CandidateName],
            failedNames: [context.FailedName],
            journalNames: [context.JournalName],
            state);

    private static PromotionOutcome CreateResidueOutcome(
        string phase,
        string reason,
        int exitCode,
        string destinationName,
        IEnumerable<string> names,
        string? state = null)
    {
        var ordered = names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return CreateOutcome(
            phase,
            reason,
            exitCode,
            destinationName,
            priorNames: ordered.Where(name =>
                name.StartsWith(
                    $"{destinationName}.prior-",
                    comparison)),
            candidateNames: ordered.Where(name =>
                name.StartsWith(
                    $"{destinationName}.candidate-",
                    comparison)),
            failedNames: ordered.Where(name =>
                name.StartsWith(
                    $"{destinationName}.failed-",
                    comparison)),
            journalNames: ordered.Where(name =>
                name.StartsWith(
                    $"{destinationName}.transaction-",
                    comparison)
                || name.StartsWith(
                    $".{destinationName}.transaction-",
                    comparison)),
            state);
    }

    private static List<string> PreMutationRecoveryNames(
        string candidateName,
        PromotionTreeSnapshot? candidateSnapshot,
        string journalName,
        PromotionArtifactIdentity? journalIdentity,
        Exception failure)
    {
        var names = new List<string>();
        if (candidateSnapshot is not null)
        {
            names.Add(candidateName);
        }

        if (journalIdentity is not null)
        {
            names.Add(journalName);
        }

        if (failure is PromotionArtifactConstructionException
            constructionException)
        {
            names.Add(constructionException.LogicalName);
        }

        return names;
    }

    private static List<string> BoundEscapedNames(
        IEnumerable<string>? names)
    {
        const int MaximumReportedNames = 4;
        var bounded = (names ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(MaximumReportedNames + 1)
            .ToArray();
        var result = bounded
            .Take(MaximumReportedNames)
            .Select(EscapeLogicalName)
            .ToList();
        if (bounded.Length > MaximumReportedNames)
        {
            result.Add("~more");
        }

        return result;
    }

    private static string StableFailureReason(Exception exception) =>
        exception switch
        {
            PromotionArtifactConstructionException =>
                exception.Message,
            OperationCanceledException => "promotion-cancelled",
            Win32Exception win32Exception =>
                $"promotion-win32-{win32Exception.NativeErrorCode}",
            InvalidDataException
                or CaptureSafetyException
                or PlatformNotSupportedException
                or PromotionFileSystemSafetyException =>
                exception.Message,
            UnauthorizedAccessException =>
                "promotion-access-refused",
            IOException => "promotion-io-failure",
            _ => "promotion-unexpected-failure"
        };

    private static string SafeLogicalName(string? path)
    {
        try
        {
            var trimmed = (path ?? string.Empty).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmed);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static void MoveCandidateAsideIfNecessary(
        IPromotionFileSystemLease lease,
        PromotionTransactionContext context)
    {
        var candidateIdentity = context.CandidateSnapshot.RootIdentity;
        var destinationIdentity =
            lease.TryGetSiblingIdentity(context.DestinationName);
        var candidatePathIdentity =
            lease.TryGetSiblingIdentity(context.CandidateName);
        var failedIdentity =
            lease.TryGetSiblingIdentity(context.FailedName);

        if (destinationIdentity == candidateIdentity)
        {
            if (failedIdentity is not null)
            {
                throw new PromotionFileSystemSafetyException(
                    "promotion-failed-path-occupied");
            }

            lease.MoveSibling(
                context.DestinationName,
                context.FailedName,
                candidateIdentity);
            return;
        }

        if (failedIdentity == candidateIdentity)
        {
            return;
        }

        if (candidatePathIdentity == candidateIdentity)
        {
            return;
        }

        if (destinationIdentity is not null
            && context.PriorSnapshot?.RootIdentity
            != destinationIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-destination-identity-ambiguous");
        }
    }

    private static void RestorePriorIfNecessary(
        IPromotionFileSystemLease lease,
        PromotionTransactionContext context)
    {
        var priorSnapshot = context.PriorSnapshot
            ?? throw new InvalidOperationException(
                "promotion-prior-snapshot-missing");
        var destinationIdentity =
            lease.TryGetSiblingIdentity(context.DestinationName);
        if (destinationIdentity == priorSnapshot.RootIdentity)
        {
            return;
        }

        if (destinationIdentity is not null)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-destination-not-empty-before-restore");
        }

        var priorIdentity =
            lease.TryGetSiblingIdentity(context.PriorName);
        if (priorIdentity != priorSnapshot.RootIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-prior-identity-changed");
        }

        EnsureSnapshotsEqual(
            priorSnapshot,
            lease.CaptureSiblingTree(context.PriorName),
            requireIdentity: true,
            "promotion-prior-identity-changed");
        lease.MoveSibling(
            context.PriorName,
            context.DestinationName,
            priorSnapshot.RootIdentity);
    }

    private static void CleanupRollbackArtifacts(
        IPromotionFileSystemLease lease,
        PromotionTransactionContext context)
    {
        lease.DeleteOwnedTreeIfRootIdentity(
            context.CandidateName,
            context.CandidateSnapshot.RootIdentity);
        lease.DeleteOwnedTreeIfRootIdentity(
            context.FailedName,
            context.CandidateSnapshot.RootIdentity);
        lease.DeleteFileIfIdentity(
            context.JournalName,
            context.JournalIdentity);
    }

    private static void DeleteTreeWherePresent(
        IPromotionFileSystemLease lease,
        string siblingName,
        PromotionTreeSnapshot expected)
    {
        var identity = lease.TryGetSiblingIdentity(siblingName);
        if (identity is null)
        {
            return;
        }

        if (identity != expected.RootIdentity)
        {
            throw new PromotionFileSystemSafetyException(
                "promotion-cleanup-identity-changed");
        }

        lease.DeleteTreeIfExact(siblingName, expected);
    }

    private static bool TryCleanupPreMutation(
        IPromotionFileSystemLease lease,
        string candidateName,
        PromotionTreeSnapshot? candidateSnapshot,
        string journalName,
        PromotionArtifactIdentity? journalIdentity,
        out Exception? cleanupException)
    {
        try
        {
            if (candidateSnapshot is not null)
            {
                lease.DeleteOwnedTreeIfRootIdentity(
                    candidateName,
                    candidateSnapshot.RootIdentity);
            }

            if (journalIdentity is not null)
            {
                lease.DeleteFileIfIdentity(
                    journalName,
                    journalIdentity.Value);
            }

            cleanupException = null;
            return true;
        }
        catch (Exception ex)
        {
            cleanupException = ex;
            return false;
        }
    }

    private static void EnsureSnapshotsEqual(
        PromotionTreeSnapshot expected,
        PromotionTreeSnapshot actual,
        bool requireIdentity,
        string reason)
    {
        EnsureFileManifestsEqual(
            expected.Files,
            actual.Files,
            reason);
        if (requireIdentity
            && (expected.RootIdentity != actual.RootIdentity
                || !expected.Entries.SequenceEqual(actual.Entries)))
        {
            throw new PromotionFileSystemSafetyException(reason);
        }
    }

    private static void EnsureFileManifestsEqual(
        IReadOnlyList<BaselineFileManifestEntry> expected,
        IReadOnlyList<BaselineFileManifestEntry> actual,
        string reason)
    {
        if (!expected.OrderBy(
                    item => item.RelativePath,
                    StringComparer.Ordinal)
                .SequenceEqual(actual.OrderBy(
                    item => item.RelativePath,
                    StringComparer.Ordinal)))
        {
            throw new InvalidDataException(reason);
        }
    }

    private static string SiblingPath(
        string destinationRoot,
        string siblingName)
    {
        if (!IsSafeSiblingName(siblingName))
        {
            throw new InvalidDataException(
                "promotion-sibling-name-invalid");
        }

        return Path.Combine(
            Path.GetDirectoryName(destinationRoot)
                ?? throw new InvalidDataException(
                    "promotion-destination-parent-missing"),
            siblingName);
    }

    private static bool IsSafeSiblingName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !Path.IsPathRooted(name)
        && Path.GetFileName(name).Equals(
            name,
            StringComparison.Ordinal)
        && name is not "." and not "..";

    private static bool IsTrustRefusal(Exception exception) =>
        exception is PromotionFileSystemSafetyException
            or CaptureSafetyException
            or UnauthorizedAccessException
            or PlatformNotSupportedException;

    private static string LogicalName(string path)
    {
        var trimmed = path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return EscapeLogicalName(Path.GetFileName(trimmed));
    }

    private static string EscapeLogicalName(string? value)
    {
        const int MaximumEscapedLength = 192;
        const string TruncationMarker = "~";
        var builder = new StringBuilder(
            Math.Min(value?.Length ?? 0, MaximumEscapedLength));
        foreach (var character in value ?? string.Empty)
        {
            var safe = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.'
                or '_'
                or '-';
            var encoded = safe
                ? character.ToString()
                : $"\\u{(int)character:x4}";
            if (builder.Length + encoded.Length
                > MaximumEscapedLength
                  - TruncationMarker.Length)
            {
                builder.Append('~');
                break;
            }

            builder.Append(encoded);
        }

        return builder.ToString();
    }
}
