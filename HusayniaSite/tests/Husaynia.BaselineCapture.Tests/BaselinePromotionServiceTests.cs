using Husaynia.BaselineCapture;
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Husaynia.BaselineCapture.Tests;

public sealed class BaselinePromotionServiceTests
{
    [Fact]
    public void PhysicalTreeEnumerationRejectsOversizedFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TemporaryParent();
        try
        {
            using (var stream = File.Create(
                       Path.Combine(root, "oversized.bin")))
            {
                stream.SetLength(
                    BaselineEvidenceValidator.MaximumEvidenceFileBytes + 1);
            }

            var exception = Assert.Throws<InvalidDataException>(
                () => new PhysicalPromotionFileSystem()
                    .EnumerateTree(root));

            Assert.Equal(
                "promotion-tree-limit-exceeded",
                exception.Message);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public void PhysicalTreeEnumerationRejectsExcessiveDepth()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TemporaryParent();
        try
        {
            var current = root;
            for (var depth = 0; depth < 65; depth++)
            {
                current = Path.Combine(current, "d");
                Directory.CreateDirectory(current);
            }

            var exception = Assert.Throws<InvalidDataException>(
                () => new PhysicalPromotionFileSystem()
                    .EnumerateTree(root));

            Assert.Equal(
                "promotion-tree-depth-exceeded",
                exception.Message);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public void PhysicalTreeEnumerationAcceptsMaximumDepth()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TemporaryParent();
        try
        {
            var current = root;
            for (var depth = 0; depth < 64; depth++)
            {
                current = Path.Combine(current, "d");
                Directory.CreateDirectory(current);
            }

            Assert.Empty(
                new PhysicalPromotionFileSystem()
                    .EnumerateTree(root));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public void AnchoredSourceTraversalIgnoresAncestorReplacementAfterHandleValidation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outer = TemporaryParent();
        var sourceAncestor = Path.Combine(outer, "source-host");
        var source = Path.Combine(sourceAncestor, "source");
        var parkedAncestor = Path.Combine(outer, "source-host-parked");
        var destinationParent = TemporaryParent();
        var destination = Path.Combine(destinationParent, "baseline");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, "artifact.txt"),
            "trusted-source");
        var expected = new PhysicalPromotionFileSystem()
            .EnumerateTree(source);
        var policy = new AncestorSwappingTrustPolicy(
            sourceAncestor,
            parkedAncestor);
        try
        {
            using var lease = new PhysicalPromotionFileSystem(policy)
                .OpenLease(source, destination);

            var actual = lease.CaptureSourceTree();

            Assert.Equal(expected, actual.Files);
            Assert.True(policy.Swapped);
            Assert.Equal(
                "attacker-source",
                File.ReadAllText(Path.Combine(
                    source,
                    "artifact.txt")));
        }
        finally
        {
            DeleteDirectoryWithRetry(outer);
            DeleteDirectoryWithRetry(destinationParent);
        }
    }

    [Fact]
    public async Task ValidSealedCandidatePromotesAndPostVerifiesByteExact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var expected = new PhysicalPromotionFileSystem()
            .EnumerateTree(source);
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(
                    output: output,
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.True(exitCode == 0, error.ToString());
            Assert.Equal(
                expected,
                new PhysicalPromotionFileSystem()
                    .EnumerateTree(destination));
            AssertNoTransactionArtifacts(parent);
            var exitEvent = AssertExitEvent(
                output,
                expectedExit: 0,
                expectedPhase: "complete",
                expectedReason: "promotion-complete");
            Assert.Equal(
                "baseline",
                exitEvent.GetProperty("destination").GetString());
            Assert.DoesNotContain(
                parent,
                output.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                parent,
                error.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task InitialPromotionCompletesWithoutPriorResidue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = Path.Combine(parent, "baseline");
        var expected = new PhysicalPromotionFileSystem()
            .EnumerateTree(source);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                expected,
                new PhysicalPromotionFileSystem()
                    .EnumerateTree(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task InitialPromotionPostSwapFaultRestoresNoDestination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = Path.Combine(parent, "baseline");
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point == PromotionFaultPoint.AfterSwap)
                        {
                            throw new IOException(
                                "force-initial-rollback");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.False(Directory.Exists(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task InvalidSourceFailsBeforeCopyAndLeavesBaselineUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = TemporaryParent();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Theory]
    [InlineData(PromotionFaultPoint.BeforeCopy)]
    [InlineData(PromotionFaultPoint.AfterCopyBeforeVerify)]
    [InlineData(PromotionFaultPoint.AfterCopyVerify)]
    public async Task PreSwapFaultsLeavePriorBaselineByteAndIdentityExact(
        PromotionFaultPoint faultPoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point == faultPoint)
                        {
                            throw new IOException($"fault-{point}");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Theory]
    [InlineData(PromotionFaultPoint.AfterBackupMove)]
    [InlineData(PromotionFaultPoint.AfterSwap)]
    [InlineData(PromotionFaultPoint.AfterPostVerify)]
    public async Task SwapAndPostVerifyFaultsRestorePriorByteAndIdentityExact(
        PromotionFaultPoint faultPoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(
                    error: error,
                    faultInjector: point =>
                    {
                        if (point == faultPoint)
                        {
                            throw new IOException($"fault-{point}");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.True(exitCode == 1, error.ToString());
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task PriorMovePostRenameThrowRestoresPriorExactly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var fileSystem = new DecoratingFileSystem(
            lease => new PostRenameThrowingLease(
                lease,
                lease.DestinationName));
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task PriorMovePostRenameThrowWithAmbiguousAncestorReturnsFour()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var fileSystem = new DecoratingFileSystem(
            lease => new PostRenameAmbiguousLease(
                lease,
                lease.DestinationName));
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.prior-*"));
            Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.candidate-*"));
            Assert.Single(Directory.GetFiles(
                parent,
                "baseline.transaction-*.json"));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task CancellationAtAfterPostVerifyRestoresPriorExact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point
                            == PromotionFaultPoint.AfterPostVerify)
                        {
                            cancellation.Cancel();
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    cancellation.Token);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task CancellationDuringCandidateCopySelfCleansCreatedCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        using var cancellation = new CancellationTokenSource();
        var observer = new ArtifactConstructionObserver(
            candidateCreated: _ => cancellation.Cancel());
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    cancellation.Token);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task CandidatePostCreateValidationFailureSelfCleansCreatedCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var observer = new ArtifactConstructionObserver(
            afterCandidateCreateBeforeValidation: _ =>
                throw new IOException(
                    "injected-candidate-post-create-validation-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task CandidatePostCreateValidationCleanupFailureReturnsFourAndIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var error = new StringWriter();
        var observer = new ArtifactConstructionObserver(
            afterCandidateCreateBeforeValidation: _ =>
                throw new IOException(
                    "injected-candidate-post-create-validation-failure"),
            beforeConstructionCleanup: _ =>
                throw new IOException(
                    "injected-candidate-post-create-cleanup-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var firstExit = await new BaselinePromotionService(
                    fileSystem: fileSystem,
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, firstExit);
            var candidate = Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.candidate-*"));
            var exitEvent = AssertExitEvent(
                error,
                expectedExit: 4,
                expectedPhase: "candidate-copy",
                expectedReason:
                "promotion-candidate-construction-residue");
            Assert.Contains(
                Path.GetFileName(candidate),
                exitEvent.GetProperty("candidates")
                    .EnumerateArray()
                    .Select(item => item.GetString()),
                StringComparer.Ordinal);
            var beforeRetry =
                SnapshotDirectoryExcludingLock(parent);

            var secondExit = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, secondExit);
            Assert.Equal(
                beforeRetry,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task DestinationPostVerificationFailureRestoresPriorExact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point == PromotionFaultPoint.AfterSwap)
                        {
                            File.Delete(Path.Combine(
                                destination,
                                "checksums.sha256"));
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("changed")]
    public async Task SameProcessRollbackDoesNotTrustDiagnosticJournal(
        string journalMutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point != PromotionFaultPoint.AfterSwap)
                        {
                            return;
                        }

                        var journal = Assert.Single(
                            Directory.GetFiles(
                                parent,
                                "baseline.transaction-*.json"));
                        if (journalMutation == "missing")
                        {
                            File.Delete(journal);
                        }
                        else if (journalMutation == "corrupt")
                        {
                            File.WriteAllText(journal, "{broken");
                        }
                        else
                        {
                            File.AppendAllText(
                                journal,
                                Environment.NewLine + "changed");
                        }

                        throw new IOException("force-rollback");
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task RestoreFailureReturnsFourAndPreservesLogicalRecoveryPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var error = new StringWriter();
        var fileSystem = new DecoratingFileSystem(
            lease => new RestoreFailingLease(
                lease,
                destinationName: "baseline"));
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem,
                    error: error,
                    faultInjector: point =>
                    {
                        if (point == PromotionFaultPoint.AfterSwap)
                        {
                            throw new IOException("force-rollback");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Single(
                Directory.GetDirectories(
                    parent,
                    "baseline.prior-*"));
            Assert.Single(
                Directory.GetDirectories(
                    parent,
                    "baseline.failed-*"));
            Assert.Single(
                Directory.GetFiles(
                    parent,
                    "baseline.transaction-*.json"));
            Assert.Contains(
                "promotion-restore-failed",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                parent,
                error.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task PriorCleanupFailurePreservesVerifiedDestinationAndReturnsFour()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var expected = new PhysicalPromotionFileSystem()
            .EnumerateTree(source);
        var error = new StringWriter();
        var fileSystem = new DecoratingFileSystem(
            lease => new PriorCleanupFailingLease(lease));
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem,
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Equal(
                expected,
                new PhysicalPromotionFileSystem()
                    .EnumerateTree(destination));
            Assert.Single(
                Directory.GetDirectories(
                    parent,
                    "baseline.prior-*"));
            Assert.Single(
                Directory.GetFiles(
                    parent,
                    "baseline.transaction-*.json"));
            Assert.Contains(
                "verified-destination-preserved",
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task JournalFlushFailureSelfCleansCreatedJournal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var observer = new ArtifactConstructionObserver(
            beforeDiagnosticFlush: _ =>
                throw new IOException("injected-journal-flush-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task JournalPostCreateValidationFailureSelfCleansCreatedJournal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var observer = new ArtifactConstructionObserver(
            afterJournalCreateBeforeValidation: _ =>
                throw new IOException(
                    "injected-journal-post-create-validation-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task JournalPostCreateValidationCleanupFailureReturnsFourAndIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var error = new StringWriter();
        var observer = new ArtifactConstructionObserver(
            afterJournalCreateBeforeValidation: _ =>
                throw new IOException(
                    "injected-journal-post-create-validation-failure"),
            beforeConstructionCleanup: _ =>
                throw new IOException(
                    "injected-journal-post-create-cleanup-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var firstExit = await new BaselinePromotionService(
                    fileSystem: fileSystem,
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, firstExit);
            Assert.Empty(Directory.GetDirectories(
                parent,
                "baseline.candidate-*"));
            var journal = Assert.Single(Directory.GetFiles(
                parent,
                "baseline.transaction-*.json"));
            var exitEvent = AssertExitEvent(
                error,
                expectedExit: 4,
                expectedPhase: "journal-create",
                expectedReason:
                "promotion-journal-construction-residue");
            Assert.Contains(
                Path.GetFileName(journal),
                exitEvent.GetProperty("journals")
                    .EnumerateArray()
                    .Select(item => item.GetString()),
                StringComparer.Ordinal);
            var beforeRetry =
                SnapshotDirectoryExcludingLock(parent);

            var secondExit = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, secondExit);
            Assert.Equal(
                beforeRetry,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task JournalConstructionCleanupFailureReturnsFourWithRecoveryName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var error = new StringWriter();
        var observer = new ArtifactConstructionObserver(
            beforeDiagnosticFlush: _ =>
                throw new IOException("injected-journal-flush-failure"),
            beforeConstructionCleanup: _ =>
                throw new IOException("injected-journal-cleanup-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem,
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            Assert.Empty(Directory.GetDirectories(
                parent,
                "baseline.candidate-*"));
            var journal = Assert.Single(Directory.GetFiles(
                parent,
                "baseline.transaction-*.json"));
            var exitEvent = AssertExitEvent(
                error,
                expectedExit: 4,
                expectedPhase: "journal-create",
                expectedReason:
                "promotion-journal-construction-residue");
            Assert.Contains(
                Path.GetFileName(journal),
                exitEvent.GetProperty("journals")
                    .EnumerateArray()
                    .Select(item => item.GetString()),
                StringComparer.Ordinal);
            Assert.DoesNotContain(
                parent,
                error.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task JournalConstructionResidueIsRefusedByNextInvocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var observer = new ArtifactConstructionObserver(
            beforeDiagnosticFlush: _ =>
                throw new IOException("injected-journal-flush-failure"),
            beforeConstructionCleanup: _ =>
                throw new IOException("injected-journal-cleanup-failure"));
        var fileSystem = new PhysicalPromotionFileSystem(
            trustPolicy: null,
            observer: observer);
        try
        {
            var firstExit = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);
            Assert.Equal(4, firstExit);
            var beforeRetry =
                SnapshotDirectoryExcludingLock(parent);

            var secondExit = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, secondExit);
            Assert.Equal(
                beforeRetry,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task ThrowingSuccessWriterCannotUndoCommittedBaseline()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var expected = new PhysicalPromotionFileSystem()
            .EnumerateTree(source);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    output: new ThrowingTextWriter())
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(
                expected,
                new PhysicalPromotionFileSystem()
                    .EnumerateTree(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task ThrowingFailureWriterCannotPreventRollback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    error: new ThrowingTextWriter(),
                    faultInjector: point =>
                    {
                        if (point == PromotionFaultPoint.AfterSwap)
                        {
                            throw new IOException("force-rollback");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task ThrowingFailureWriterCannotMaskRollbackFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var fileSystem = new DecoratingFileSystem(
            lease => new RestoreFailingLease(
                lease,
                destinationName: "baseline"));
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem,
                    error: new ThrowingTextWriter(),
                    faultInjector: point =>
                    {
                        if (point == PromotionFaultPoint.AfterSwap)
                        {
                            throw new IOException("force-rollback");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.prior-*"));
            Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.failed-*"));
            Assert.Single(Directory.GetFiles(
                parent,
                "baseline.transaction-*.json"));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task ThrowingFailureWriterCannotMaskResidueRefusal()
    {
        var exitCode = await new BaselinePromotionService(
                fileSystem: new ResidueOnlyFileSystem(
                    "baseline.candidate-preserve"),
                error: new ThrowingTextWriter())
            .PromoteAsync(
                Path.Combine(Path.GetTempPath(), "logical-source"),
                Path.Combine(Path.GetTempPath(), "baseline"),
                CancellationToken.None);

        Assert.Equal(4, exitCode);
    }

    [Fact]
    public async Task EveryMajorExitEmitsOneStructuredSettledEvent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        try
        {
            var successParent = TemporaryParent();
            var successDestination = CreatePriorBaseline(successParent);
            try
            {
                var output = new StringWriter();
                var successExit = await new BaselinePromotionService(
                        output: output)
                    .PromoteAsync(
                        source,
                        successDestination,
                        CancellationToken.None);

                Assert.Equal(0, successExit);
                AssertExitEvent(
                    output,
                    expectedExit: 0,
                    expectedPhase: "complete",
                    expectedReason: "promotion-complete");
            }
            finally
            {
                DeleteDirectoryWithRetry(successParent);
            }

            var failureParent = TemporaryParent();
            var failureDestination = CreatePriorBaseline(failureParent);
            var failureBefore = SnapshotPath(failureDestination);
            try
            {
                var error = new StringWriter();
                var failureExit = await new BaselinePromotionService(
                        error: error,
                        faultInjector: point =>
                        {
                            if (point == PromotionFaultPoint.AfterSwap)
                            {
                                throw new IOException(
                                    "force-rollback");
                            }
                        })
                    .PromoteAsync(
                        source,
                        failureDestination,
                        CancellationToken.None);

                Assert.Equal(1, failureExit);
                Assert.Equal(
                    failureBefore,
                    SnapshotPath(failureDestination));
                AssertExitEvent(
                    error,
                    expectedExit: 1,
                    expectedPhase: "rollback-complete",
                    expectedReason: "promotion-io-failure");
            }
            finally
            {
                DeleteDirectoryWithRetry(failureParent);
            }

            var refusalError = new StringWriter();
            var refusalExit = await new BaselinePromotionService(
                    error: refusalError)
                .PromoteAsync(
                    source,
                    source,
                    CancellationToken.None);
            Assert.Equal(2, refusalExit);
            AssertExitEvent(
                refusalError,
                expectedExit: 2,
                expectedPhase: "path-validation",
                expectedReason:
                "promotion-same-volume-distinct-paths-required");

            var recoveryError = new StringWriter();
            var recoveryExit = await new BaselinePromotionService(
                    fileSystem: new ResidueOnlyFileSystem(
                        "baseline.prior-preserve"),
                    error: recoveryError)
                .PromoteAsync(
                    Path.Combine(Path.GetTempPath(), "logical-source"),
                    Path.Combine(Path.GetTempPath(), "baseline"),
                    CancellationToken.None);
            Assert.Equal(4, recoveryExit);
            var recoveryEvent = AssertExitEvent(
                recoveryError,
                expectedExit: 4,
                expectedPhase: "residue-refusal",
                expectedReason:
                "promotion-interrupted-state-manual-recovery-required");
            Assert.Equal(
                "baseline.prior-preserve",
                Assert.Single(
                    recoveryEvent.GetProperty("priors")
                        .EnumerateArray())
                    .GetString());
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
        }
    }

    [Fact]
    public async Task ConcurrentPromotionLockIsSafetyRefusal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var lockPath = Path.Combine(
            parent,
            "baseline.promotion.lock");
        try
        {
            using var held = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    public static IEnumerable<object[]> ResidueCombinations()
    {
        for (var mask = 1; mask < 16; mask++)
        {
            yield return [mask, false, false];
            yield return [mask, false, true];
            yield return [mask, true, false];
            yield return [mask, true, true];
        }
    }

    [Theory]
    [MemberData(nameof(ResidueCombinations))]
    public async Task EveryCrossProcessResidueCombinationIsPreserved(
        int mask,
        bool destinationPresent,
        bool temporaryJournal)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = TemporaryParent();
        File.WriteAllText(Path.Combine(source, "source.txt"), "source");
        var parent = TemporaryParent();
        var destination = Path.Combine(parent, "BaSeLiNe");
        if (destinationPresent)
        {
            CreateLegacyBaselineAt(destination);
        }

        CreateMixedCaseResidue(
            parent,
            mask,
            temporaryJournal);
        CreateNearPrefixArtifacts(parent);
        var before = SnapshotDirectoryExcludingLock(parent);
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Equal(
                before,
                SnapshotDirectoryExcludingLock(parent));
            var exitEvent = AssertExitEvent(
                error,
                expectedExit: 4,
                expectedPhase: "residue-refusal",
                expectedReason:
                "promotion-interrupted-state-manual-recovery-required");
            AssertRecoveryNames(
                exitEvent,
                "priors",
                (mask & 2) != 0
                    ? ["BASELINE.PriOr-forged"]
                    : []);
            AssertRecoveryNames(
                exitEvent,
                "candidates",
                (mask & 4) != 0
                    ? ["baseline.CANDIDATE-forged"]
                    : []);
            AssertRecoveryNames(
                exitEvent,
                "failed",
                (mask & 8) != 0
                    ? ["BasELine.FaIlEd-forged"]
                    : []);
            AssertRecoveryNames(
                exitEvent,
                "journals",
                (mask & 1) != 0
                    ?
                    [
                        temporaryJournal
                            ? ".bAsElInE.TrAnSaCtIoN-forged.json.TmP"
                            : "bAsElInE.TrAnSaCtIoN-forged.json"
                    ]
                    : []);
            Assert.DoesNotContain(
                parent,
                error.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Theory]
    [InlineData("baseline.priorish-forged")]
    [InlineData("baseline.candidateish-forged")]
    [InlineData("baseline.failedish-forged")]
    [InlineData("baseline.transactionish-forged.json")]
    [InlineData(".baseline.transactionish-forged.json.tmp")]
    [InlineData(".baseline.transaction-forged.json.temp")]
    public void NearPromotionResiduePrefixesAreIgnored(
        string nearPrefixName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = TemporaryParent();
        var parent = TemporaryParent();
        var destination = Path.Combine(parent, "BaSeLiNe");
        var nearPrefixPath = Path.Combine(parent, nearPrefixName);
        if (nearPrefixName.StartsWith('.'))
        {
            File.WriteAllText(nearPrefixPath, "preserve");
        }
        else
        {
            CreateResidueDirectory(
                parent,
                nearPrefixName,
                "preserve");
        }

        var before = SnapshotDirectoryExcludingLock(parent);
        try
        {
            using (var lease = new PhysicalPromotionFileSystem()
                       .OpenLease(source, destination))
            {
                Assert.Empty(lease.ListPromotionResidue());
            }

            Assert.Equal(
                before,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Theory]
    [InlineData(".baseline.transaction-forged.json.deadbeef.tmp")]
    [InlineData("baseline.failed-prior-forged")]
    public async Task TemporaryAndFailedPriorResidueArePreserved(
        string residueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = TemporaryParent();
        var parent = TemporaryParent();
        var destination = Path.Combine(parent, "baseline");
        var residue = Path.Combine(parent, residueName);
        if (residueName.Contains(
                "failed-prior",
                StringComparison.Ordinal))
        {
            Directory.CreateDirectory(residue);
            File.WriteAllText(
                Path.Combine(residue, "prior.txt"),
                "preserve");
        }
        else
        {
            File.WriteAllText(residue, "preserve");
        }

        var before = SnapshotDirectoryExcludingLock(parent);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Equal(
                before,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task CrossProcessResiduePrecedesDestinationSecurityInspection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        AddUnsafeInheritOnlyRule(destination, "S-1-1-0");
        CreateResidueDirectory(
            parent,
            "baseline.candidate-forged",
            "preserve");
        var before = SnapshotDirectoryExcludingLock(parent);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Equal(
                before,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task ForgedJournalAndPriorCannotBecomeCanonical()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = TemporaryParent();
        var parent = TemporaryParent();
        var destination = Path.Combine(parent, "baseline");
        var prior = Path.Combine(
            parent,
            "baseline.prior-forged");
        Directory.CreateDirectory(prior);
        File.WriteAllText(
            Path.Combine(prior, "poison.txt"),
            "workspace-forgery");
        File.WriteAllText(
            Path.Combine(
                parent,
                "baseline.transaction-forged.json"),
            """
            {"phase":"priorMoveCompleted","prior":"baseline.prior-forged"}
            """);
        var before = SnapshotDirectoryExcludingLock(parent);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.False(Directory.Exists(destination));
            Assert.Equal(
                before,
                SnapshotDirectoryExcludingLock(parent));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Theory]
    [InlineData(PromotionFaultPoint.AfterBackupMove, false)]
    [InlineData(PromotionFaultPoint.AfterSwap, false)]
    [InlineData(PromotionFaultPoint.AfterPostVerify, false)]
    [InlineData(PromotionFaultPoint.AfterSwap, true)]
    public async Task CrashedPromoterResidueIsPreservedByNextProcess(
        PromotionFaultPoint crashPoint,
        bool useDeepOsTempTopology)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var originalSource =
            await BaselineTestFixture.CreateValidCandidateAsync();
        var nonce = Guid.NewGuid().ToString("N");
        var authorizedRoot = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-promotion-crash-{nonce}");
        Directory.CreateDirectory(authorizedRoot);
        var source = Path.Combine(authorizedRoot, "source");
        Directory.Move(originalSource, source);
        var parent = useDeepOsTempTopology
            ? CreateDeepCrashParent(authorizedRoot)
            : Path.Combine(authorizedRoot, "destination-parent");
        Directory.CreateDirectory(parent);
        var destination = CreatePriorBaseline(parent);
        try
        {
            var harness = CrashHarnessPath();
            Assert.True(File.Exists(harness), harness);
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(harness);
            startInfo.ArgumentList.Add(
                "--husaynia-promotion-crash-test");
            startInfo.ArgumentList.Add(nonce);
            startInfo.ArgumentList.Add(authorizedRoot);
            startInfo.ArgumentList.Add(source);
            startInfo.ArgumentList.Add(destination);
            startInfo.ArgumentList.Add(crashPoint.ToString());
            using var child = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "promotion-crash-child-start-failed");
            await child.WaitForExitAsync();
            Assert.Equal(97, child.ExitCode);

            var journal = Assert.Single(
                Directory.GetFiles(
                    parent,
                    "baseline.transaction-*.json"));
            AssertPrivatePromotionArtifact(journal);
            var candidate = Directory.GetDirectories(
                parent,
                "baseline.candidate-*");
            AssertPrivatePromotionArtifact(
                candidate.Length == 1
                    ? candidate[0]
                    : destination);
            var afterCrash =
                SnapshotDirectoryExcludingLock(parent);
            var error = new StringWriter();
            var exitCode = await new BaselinePromotionService(
                    error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.Equal(
                afterCrash,
                SnapshotDirectoryExcludingLock(parent));
            Assert.Contains(
                "promotion-interrupted-state-manual-recovery-required",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                parent,
                error.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryWithRetry(authorizedRoot);
        }
    }

    [Fact]
    public async Task CrashHarnessRefusesArbitraryPathsWithoutSentinel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(CrashHarnessPath());
            startInfo.ArgumentList.Add(source);
            startInfo.ArgumentList.Add(destination);
            startInfo.ArgumentList.Add(
                PromotionFaultPoint.BeforeCopy.ToString());
            using var child = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "promotion-crash-child-start-failed");
            await child.WaitForExitAsync();

            Assert.Equal(2, child.ExitCode);
            Assert.True(Directory.Exists(destination));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task CrashHarnessRefusesPathOutsideAuthorizedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var nonce = Guid.NewGuid().ToString("N");
        var authorizedRoot = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-promotion-crash-{nonce}");
        var parent = Path.Combine(
            authorizedRoot,
            "destination-parent");
        Directory.CreateDirectory(parent);
        var destination = CreatePriorBaseline(parent);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(CrashHarnessPath());
            startInfo.ArgumentList.Add(
                "--husaynia-promotion-crash-test");
            startInfo.ArgumentList.Add(nonce);
            startInfo.ArgumentList.Add(authorizedRoot);
            startInfo.ArgumentList.Add(source);
            startInfo.ArgumentList.Add(destination);
            startInfo.ArgumentList.Add(
                PromotionFaultPoint.BeforeCopy.ToString());
            using var child = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "promotion-crash-child-start-failed");
            await child.WaitForExitAsync();

            Assert.Equal(2, child.ExitCode);
            Assert.True(Directory.Exists(destination));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(authorizedRoot);
        }
    }

    [Fact]
    public async Task SourceHardLinkIsRefusedAndExternalTargetIsUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var external = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-promotion-external-{Guid.NewGuid():N}.md");
        var sourceReadme = Path.Combine(source, "README.md");
        var bytes = await File.ReadAllBytesAsync(sourceReadme);
        await File.WriteAllBytesAsync(external, bytes);
        File.Delete(sourceReadme);
        Assert.True(
            CreateHardLinkW(sourceReadme, external, IntPtr.Zero));
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(external));
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
            File.Delete(external);
        }
    }

    [Fact]
    public async Task PriorHardLinkIsRefusedAndExternalTargetIsUnchanged()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var external = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-promotion-external-{Guid.NewGuid():N}.txt");
        var priorFile = Path.Combine(destination, "prior.txt");
        var bytes = await File.ReadAllBytesAsync(priorFile);
        await File.WriteAllBytesAsync(external, bytes);
        File.Delete(priorFile);
        Assert.True(
            CreateHardLinkW(priorFile, external, IntPtr.Zero));
        var before = SnapshotPath(destination);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(external));
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
            File.Delete(external);
        }
    }

    [Fact]
    public async Task CandidateHardLinkIsRefusedAndCleanedWithoutChangingExternalTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var external = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-promotion-external-{Guid.NewGuid():N}.md");
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(source, "README.md"));
        await File.WriteAllBytesAsync(external, bytes);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point
                            != PromotionFaultPoint.AfterCopyBeforeVerify)
                        {
                            return;
                        }

                        var candidate = Assert.Single(
                            Directory.GetDirectories(
                                parent,
                                "baseline.candidate-*"));
                        var candidateReadme = Path.Combine(
                            candidate,
                            "README.md");
                        File.Delete(candidateReadme);
                        Assert.True(CreateHardLinkW(
                            candidateReadme,
                            external,
                            IntPtr.Zero));
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(external));
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
            File.Delete(external);
        }
    }

    [Fact]
    public async Task CandidateReparsePointIsRefusedAndCleanedWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var target = TemporaryParent();
        var marker = Path.Combine(target, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(
                    error: error,
                    faultInjector: point =>
                    {
                        if (point
                            != PromotionFaultPoint.AfterCopyBeforeVerify)
                        {
                            return;
                        }

                        var candidate = Assert.Single(
                            Directory.GetDirectories(
                                parent,
                                "baseline.candidate-*"));
                        CreateJunction(
                            Path.Combine(candidate, "hostile-link"),
                            target);
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.True(exitCode == 2, error.ToString());
            Assert.Equal(
                "preserve",
                await File.ReadAllTextAsync(marker));
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            foreach (var candidate in Directory.GetDirectories(
                         parent,
                         "baseline.candidate-*"))
            {
                var link = Path.Combine(candidate, "hostile-link");
                if (Directory.Exists(link))
                {
                    RemoveJunction(link);
                }
            }

            DeleteDirectoryWithRetry(parent);
            DeleteDirectoryWithRetry(target);
        }
    }

    [Fact]
    public async Task PriorReparsePointIsRefusedWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var target = TemporaryParent();
        var marker = Path.Combine(target, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");
        var link = Path.Combine(destination, "hostile-link");
        try
        {
            CreateJunction(link, target);
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(
                "preserve",
                await File.ReadAllTextAsync(marker));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
            DeleteDirectoryWithRetry(target);
        }
    }

    [Fact]
    public async Task SourceReparsePointIsRefusedWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var target = TemporaryParent();
        var marker = Path.Combine(target, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");
        var link = Path.Combine(source, "hostile-link");
        try
        {
            CreateJunction(link, target);
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(
                "preserve",
                await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
            DeleteDirectoryWithRetry(target);
        }
    }

    [Fact]
    public async Task DestinationReparsePointIsRefusedWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = TemporaryParent();
        var parent = TemporaryParent();
        var target = TemporaryParent();
        var destination = Path.Combine(parent, "baseline");
        var marker = Path.Combine(target, "marker.txt");
        await File.WriteAllTextAsync(marker, "preserve");
        try
        {
            CreateJunction(destination, target);
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(
                "preserve",
                await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination);
            }

            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
            DeleteDirectoryWithRetry(target);
        }
    }

    [Fact]
    public async Task HostileWritableAncestorChainIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var outer = TemporaryParent();
        var parent = Path.Combine(outer, "trusted-parent");
        Directory.CreateDirectory(parent);
        PhysicalPromotionFileSystemTrustPolicy
            .ProtectArtifact(parent);
        MakeDirectoryDeletableByBuiltinUsers(outer);
        var destination = Path.Combine(parent, "baseline");
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Contains(
                "promotion-filesystem-permissions-untrusted",
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(outer);
        }
    }

    [Theory]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-1-0")]
    public async Task UnsafeInheritOnlyParentAclIsRefused(
        string sid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        AddUnsafeInheritOnlyRule(parent, sid);
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            Assert.Contains(
                "promotion-filesystem-permissions-untrusted",
                error.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task UnsafeInheritOnlyDestinationAclIsRefusedBeforeSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        AddUnsafeInheritOnlyRule(
            destination,
            "S-1-1-0");
        var error = new StringWriter();
        try
        {
            var exitCode = await new BaselinePromotionService(error: error)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            Assert.Contains(
                "promotion-filesystem-permissions-untrusted",
                error.ToString(),
                StringComparison.Ordinal);
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task UnsafeDestinationChildAclIsRefusedBeforeManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        AddUnsafeInheritOnlyRule(
            Path.Combine(destination, "nested"),
            "S-1-1-0");
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task UnsafePriorChildAclIsPreservedAndReturnsFour()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point
                            != PromotionFaultPoint.AfterBackupMove)
                        {
                            return;
                        }

                        var prior = Assert.Single(
                            Directory.GetDirectories(
                                parent,
                                "baseline.prior-*"));
                        AddUnsafeInheritOnlyRule(
                            Path.Combine(prior, "nested"),
                            "S-1-1-0");
                        throw new IOException(
                            "force-prior-security-recheck");
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(4, exitCode);
            Assert.False(Directory.Exists(destination));
            Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.prior-*"));
            Assert.Single(Directory.GetDirectories(
                parent,
                "baseline.candidate-*"));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task RollbackDoesNotMutateLegacyDestinationAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = new DirectoryInfo(destination)
            .GetAccessControl(
                AccessControlSections.Owner
                | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        try
        {
            var exitCode = await new BaselinePromotionService(
                    faultInjector: point =>
                    {
                        if (point == PromotionFaultPoint.AfterSwap)
                        {
                            throw new IOException("force-rollback");
                        }
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.Equal(
                before,
                new DirectoryInfo(destination)
                    .GetAccessControl(
                        AccessControlSections.Owner
                        | AccessControlSections.Access)
                    .GetSecurityDescriptorBinaryForm());
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task InjectedAncestorIdentityChangeFailsBeforeCanonicalMove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var before = SnapshotPath(destination);
        var fileSystem = new DecoratingFileSystem(
            lease => new ParentIdentityChangingLease(lease));
        try
        {
            var exitCode = await new BaselinePromotionService(
                    fileSystem: fileSystem)
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(2, exitCode);
            Assert.Equal(before, SnapshotPath(destination));
            AssertNoTransactionArtifacts(parent);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task JournalAndDiagnosticsContainOnlyLogicalUnsignedState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        var error = new StringWriter();
        string? journal = null;
        try
        {
            var exitCode = await new BaselinePromotionService(
                    error: error,
                    faultInjector: point =>
                    {
                        if (point
                            != PromotionFaultPoint.AfterBackupMove)
                        {
                            return;
                        }

                        var path = Assert.Single(
                            Directory.GetFiles(
                                parent,
                                "baseline.transaction-*.json"));
                        journal = File.ReadAllText(path);
                        throw new IOException("inspect-journal");
                    })
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.NotNull(journal);
            Assert.DoesNotContain(
                parent,
                journal,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Path.GetDirectoryName(source)!,
                journal,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "keyId",
                journal,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "signature",
                journal,
                StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(journal);
            Assert.Equal(
                "husaynia-promotion-journal-diagnostic",
                document.RootElement
                    .GetProperty("schema")
                    .GetString());
            Assert.Equal(
                "prepared",
                document.RootElement
                    .GetProperty("phase")
                    .GetString());
            Assert.Equal(
                "baseline",
                document.RootElement
                    .GetProperty("destinationDirectoryName")
                    .GetString());
            Assert.DoesNotContain(
                parent,
                error.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    [Fact]
    public async Task RecoveryDiagnosticsEscapeAndBoundLogicalNames()
    {
        var hostileName =
            "baseline.prior-safe\r\ninjected=true;"
            + new string('x', 2_048);
        var error = new StringWriter();
        var exitCode = await new BaselinePromotionService(
                fileSystem: new ResidueOnlyFileSystem(hostileName),
                error: error)
            .PromoteAsync(
                Path.Combine(Path.GetTempPath(), "logical-source"),
                Path.Combine(Path.GetTempPath(), "baseline"),
                CancellationToken.None);

        var text = error.ToString();
        Assert.Equal(4, exitCode);
        Assert.DoesNotContain(
            "\r\ninjected=true",
            text,
            StringComparison.Ordinal);
        var exitEvent = AssertExitEvent(
            error,
            expectedExit: 4,
            expectedPhase: "residue-refusal",
            expectedReason:
            "promotion-interrupted-state-manual-recovery-required");
        var prior = Assert.Single(
                exitEvent.GetProperty("priors").EnumerateArray())
            .GetString();
        Assert.NotNull(prior);
        Assert.Contains(
            "\\u000d\\u000a",
            prior,
            StringComparison.Ordinal);
        Assert.True(
            text.Length < 1_024,
            text.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PromotionCreatesNoLocalApplicationDataSecurityArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var securityStore = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Husaynia",
            "BaselineCapture",
            "promotion-security");
        var before = SnapshotOptionalPath(securityStore);
        var source = await BaselineTestFixture.CreateValidCandidateAsync();
        var parent = TemporaryParent();
        var destination = CreatePriorBaseline(parent);
        try
        {
            var exitCode = await new BaselinePromotionService()
                .PromoteAsync(
                    source,
                    destination,
                    CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(before, SnapshotOptionalPath(securityStore));
        }
        finally
        {
            DeleteDirectoryWithRetry(source);
            DeleteDirectoryWithRetry(parent);
        }
    }

    private static JsonElement AssertExitEvent(
        StringWriter writer,
        int expectedExit,
        string expectedPhase,
        string expectedReason)
    {
        var lines = writer.ToString().Split(
            [Environment.NewLine],
            StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal(
            "husaynia-promotion-exit",
            root.GetProperty("schema").GetString());
        Assert.Equal(
            expectedPhase,
            root.GetProperty("phase").GetString());
        Assert.Equal(
            expectedReason,
            root.GetProperty("reason").GetString());
        Assert.Equal(
            expectedExit,
            root.GetProperty("exit").GetInt32());
        Assert.True(root.TryGetProperty("destination", out _));
        Assert.Equal(
            JsonValueKind.Array,
            root.GetProperty("priors").ValueKind);
        Assert.Equal(
            JsonValueKind.Array,
            root.GetProperty("candidates").ValueKind);
        Assert.Equal(
            JsonValueKind.Array,
            root.GetProperty("failed").ValueKind);
        Assert.Equal(
            JsonValueKind.Array,
            root.GetProperty("journals").ValueKind);
        return root.Clone();
    }

    private static void CreateMixedCaseResidue(
        string parent,
        int mask,
        bool temporaryJournal)
    {
        if ((mask & 1) != 0)
        {
            File.WriteAllText(
                Path.Combine(
                    parent,
                    temporaryJournal
                        ? ".bAsElInE.TrAnSaCtIoN-forged.json.TmP"
                        : "bAsElInE.TrAnSaCtIoN-forged.json"),
                "forged journal");
        }

        if ((mask & 2) != 0)
        {
            CreateResidueDirectory(
                parent,
                "BASELINE.PriOr-forged",
                "prior");
        }

        if ((mask & 4) != 0)
        {
            CreateResidueDirectory(
                parent,
                "baseline.CANDIDATE-forged",
                "candidate");
        }

        if ((mask & 8) != 0)
        {
            CreateResidueDirectory(
                parent,
                "BasELine.FaIlEd-forged",
                "failed");
        }
    }

    private static void CreateNearPrefixArtifacts(string parent)
    {
        CreateResidueDirectory(
            parent,
            "baseline.priorish-forged",
            "near prior");
        CreateResidueDirectory(
            parent,
            "baseline.candidateish-forged",
            "near candidate");
        CreateResidueDirectory(
            parent,
            "baseline.failedish-forged",
            "near failed");
        CreateResidueDirectory(
            parent,
            "baseline.transactionish-forged.json",
            "near journal");
        File.WriteAllText(
            Path.Combine(
                parent,
                ".baseline.transactionish-forged.json.tmp"),
            "near temporary journal");
        File.WriteAllText(
            Path.Combine(
                parent,
                ".baseline.transaction-forged.json.temp"),
            "near temporary journal suffix");
    }

    private static void AssertRecoveryNames(
        JsonElement exitEvent,
        string propertyName,
        IReadOnlyList<string> expected)
    {
        var actual = exitEvent
            .GetProperty(propertyName)
            .EnumerateArray()
            .Select(item => Assert.IsType<string>(item.GetString()))
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static void CreateResidueDirectory(
        string parent,
        string name,
        string content)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "artifact.txt"),
            content);
    }

    private static string TemporaryParent()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"husaynia-promotion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CrashHarnessPath() =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "tests",
            "Husaynia.BaselineCapture.Tests",
            "CrashHarness",
            "bin",
            "Release",
            "net10.0",
            "Husaynia.BaselineCapture.CrashHarness.dll"));

    private static string CreatePriorBaseline(string parent)
    {
        var destination = Path.Combine(parent, "baseline");
        CreateLegacyBaselineAt(destination);
        return destination;
    }

    private static string CreateDeepCrashParent(string authorizedRoot)
    {
        var current = authorizedRoot;
        for (var depth = 0;
             Path.Combine(current, "destination-parent").Length < 320;
             depth++)
        {
            current = Path.Combine(
                current,
                $"deep-{depth:D2}-{new string('x', 24)}");
        }

        var parent = Path.Combine(current, "destination-parent");
        Directory.CreateDirectory(parent);
        Assert.True(
            Path.Combine(
                parent,
                "baseline.transaction-00000000000000000000000000000000.json")
                .Length > 260);
        return parent;
    }

    private static void CreateLegacyBaselineAt(string destination)
    {
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(
            Path.Combine(destination, "nested"));
        File.WriteAllText(
            Path.Combine(destination, "prior.txt"),
            "prior baseline");
        File.WriteAllText(
            Path.Combine(destination, "nested", "evidence.txt"),
            "exact prior evidence");
    }

    private static void AssertNoTransactionArtifacts(string parent)
    {
        Assert.Empty(
            Directory.GetFileSystemEntries(
                parent,
                "baseline.prior-*"));
        Assert.Empty(
            Directory.GetFileSystemEntries(
                parent,
                "baseline.candidate-*"));
        Assert.Empty(
            Directory.GetFileSystemEntries(
                parent,
                "baseline.failed-*"));
        Assert.Empty(
            Directory.GetFileSystemEntries(
                parent,
                "baseline.transaction-*"));
        Assert.Empty(
            Directory.GetFileSystemEntries(
                parent,
                ".baseline.transaction-*.tmp"));
    }

    [SupportedOSPlatform("windows")]
    private static PathSnapshot[] SnapshotOptionalPath(
        string path) =>
        Directory.Exists(path) || File.Exists(path)
            ? SnapshotPath(path)
            : [];

    [SupportedOSPlatform("windows")]
    private static PathSnapshot[]
        SnapshotDirectoryExcludingLock(string root) =>
        SnapshotPath(root)
            .Where(entry =>
                !entry.RelativePath.Equals(
                    "baseline.promotion.lock",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

    [SupportedOSPlatform("windows")]
    private static PathSnapshot[] SnapshotPath(string root)
    {
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            return [];
        }

        var fullRoot = Path.GetFullPath(root);
        using var rootHandle = OpenSnapshotRoot(fullRoot);
        var rootInformation =
            WindowsPromotionFileSystem.GetVerifiedInformation(
                rootHandle,
                requireDirectory: null,
                requireSingleLinkFile: false);
        var entries = new List<PathSnapshot>
        {
            CaptureSnapshot(rootHandle, ".", rootInformation)
        };
        if (rootInformation.IsDirectory)
        {
            CaptureSnapshotDirectory(
                rootHandle,
                relativePrefix: string.Empty,
                entries);
        }

        return entries
            .OrderBy(
                entry => entry.RelativePath,
                StringComparer.Ordinal)
            .ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenSnapshotRoot(string fullPath)
    {
        var handle = CreateFileW(
            ToExtendedPath(fullPath),
            WindowsPromotionFileSystem.FileReadData
            | WindowsPromotionFileSystem.FileReadAttributes
            | WindowsPromotionFileSystem.Synchronize,
            0x00000007,
            IntPtr.Zero,
            3,
            0x02000000 | 0x00200000,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"snapshot-open-failed:{error}");
        }

        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static void CaptureSnapshotDirectory(
        SafeFileHandle directory,
        string relativePrefix,
        List<PathSnapshot> entries)
    {
        foreach (var name in WindowsPromotionFileSystem
                     .ReadDirectoryNames(directory)
                     .OrderBy(value => value, StringComparer.Ordinal))
        {
            using var child = WindowsPromotionFileSystem.OpenRelative(
                directory,
                name,
                WindowsPromotionFileSystem.FileReadData
                | WindowsPromotionFileSystem.FileReadAttributes
                | WindowsPromotionFileSystem.Synchronize,
                createDisposition: 1,
                createOptions: 0x00200000,
                shareAccess: 0x00000007);
            var information =
                WindowsPromotionFileSystem.GetVerifiedInformation(
                    child,
                    requireDirectory: null,
                    requireSingleLinkFile: false);
            var relativePath = string.IsNullOrEmpty(relativePrefix)
                ? name
                : $"{relativePrefix}/{name}";
            entries.Add(CaptureSnapshot(
                child,
                relativePath,
                information));
            if (information.IsDirectory)
            {
                CaptureSnapshotDirectory(
                    child,
                    relativePath,
                    entries);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static PathSnapshot CaptureSnapshot(
        SafeFileHandle handle,
        string relativePath,
        WindowsPromotionFileSystem.FileInformation information)
    {
        var hash = information.IsDirectory
            ? string.Empty
            : HashSnapshotFile(handle, information.Length);
        return new PathSnapshot(
            relativePath,
            information.IsDirectory,
            information.Identity.VolumeSerialNumber,
            information.Identity.FileId,
            information.LinkCount,
            information.IsDirectory ? 0 : information.Length,
            hash);
    }

    private static string HashSnapshotFile(
        SafeFileHandle handle,
        long length)
    {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var count = RandomAccess.Read(
                handle,
                buffer.AsSpan(
                    0,
                    (int)Math.Min(
                        buffer.Length,
                        length - offset)),
                offset);
            if (count == 0)
            {
                throw new IOException(
                    "snapshot-file-short-read");
            }

            hash.AppendData(buffer, 0, count);
            offset += count;
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static string ToExtendedPath(string fullPath)
    {
        if (fullPath.StartsWith(
                @"\\?\",
                StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(
                @"\\",
                StringComparison.Ordinal))
        {
            return $@"\\?\UNC\{fullPath[2..]}";
        }

        return $@"\\?\{fullPath}";
    }

    private static void CreateJunction(
        string junction,
        string target)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec")
                    ?? throw new InvalidOperationException(
                        "ComSpec missing"),
                $"/d /c mklink /J \"{junction}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void RemoveJunction(string junction)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(
                "fsutil",
                $"reparsepoint delete \"{junction}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Directory.Delete(junction);
    }

    [SupportedOSPlatform("windows")]
    private static void MakeDirectoryDeletableByBuiltinUsers(
        string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(
                WellKnownSidType.BuiltinUsersSid,
                null),
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AddUnsafeInheritOnlyRule(
        string path,
        string sid)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sid),
            FileSystemRights.WriteData
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles,
            InheritanceFlags.ContainerInherit
            | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertPrivatePromotionArtifact(string path)
    {
        var entries = Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(
                    path,
                    "*",
                    SearchOption.AllDirectories)
                .Prepend(path)
            : [path];
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidDataException(
                "test-current-user-missing");
        var allowed = new[]
        {
            currentUser,
            new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid,
                null),
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null)
        };
        foreach (var entry in entries)
        {
            FileSystemSecurity security = Directory.Exists(entry)
                ? new DirectoryInfo(entry).GetAccessControl(
                    AccessControlSections.Owner
                    | AccessControlSections.Access)
                : new FileInfo(entry).GetAccessControl(
                    AccessControlSections.Owner
                    | AccessControlSections.Access);
            Assert.True(
                security.AreAccessRulesProtected,
                entry);
            var owner = Assert.IsType<SecurityIdentifier>(
                security.GetOwner(
                    typeof(SecurityIdentifier)));
            Assert.Contains(owner, allowed);
            foreach (FileSystemAccessRule rule
                     in security.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: true,
                         targetType:
                         typeof(SecurityIdentifier)))
            {
                Assert.Contains(
                    (SecurityIdentifier)rule.IdentityReference,
                    allowed);
            }
        }
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 8)
            {
                Thread.Sleep(attempt * 75);
            }
            catch (UnauthorizedAccessException) when (attempt < 8)
            {
                Thread.Sleep(attempt * 75);
            }
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record PathSnapshot(
        string RelativePath,
        bool IsDirectory,
        uint VolumeSerialNumber,
        ulong FileId,
        uint LinkCount,
        long Length,
        string Sha256);

    private sealed class DecoratingFileSystem(
        Func<IPromotionFileSystemLease, IPromotionFileSystemLease>
            decorate) : IPromotionFileSystem
    {
        private readonly PhysicalPromotionFileSystem _inner = new();

        public IPromotionFileSystemLease OpenLease(
            string sourceRoot,
            string destinationRoot) =>
            decorate(_inner.OpenLease(sourceRoot, destinationRoot));

        public IReadOnlyList<BaselineFileManifestEntry> EnumerateTree(
            string root) =>
            _inner.EnumerateTree(root);
    }

    private class DelegatingLease(IPromotionFileSystemLease inner)
        : IPromotionFileSystemLease
    {
        protected IPromotionFileSystemLease Inner { get; } = inner;

        public string SourceRoot => Inner.SourceRoot;

        public string DestinationRoot => Inner.DestinationRoot;

        public string DestinationName => Inner.DestinationName;

        public IReadOnlyList<string> ListPromotionResidue() =>
            Inner.ListPromotionResidue();

        public PromotionTreeSnapshot CaptureSourceTree() =>
            Inner.CaptureSourceTree();

        public virtual PromotionTreeSnapshot? CaptureDestinationTree() =>
            Inner.CaptureDestinationTree();

        public PromotionTreeSnapshot CaptureSiblingTree(
            string siblingName) =>
            Inner.CaptureSiblingTree(siblingName);

        public virtual PromotionArtifactIdentity? TryGetSiblingIdentity(
            string siblingName) =>
            Inner.TryGetSiblingIdentity(siblingName);

        public Task<PromotionTreeSnapshot>
            CopySourceToSiblingCreateNewAsync(
                string siblingName,
                CancellationToken cancellationToken) =>
            Inner.CopySourceToSiblingCreateNewAsync(
                siblingName,
                cancellationToken);

        public PromotionArtifactIdentity CreateDiagnosticFile(
            string siblingName,
            ReadOnlySpan<byte> content) =>
            Inner.CreateDiagnosticFile(siblingName, content);

        public virtual void MoveSibling(
            string sourceSiblingName,
            string destinationSiblingName,
            PromotionArtifactIdentity expectedSourceIdentity) =>
            Inner.MoveSibling(
                sourceSiblingName,
                destinationSiblingName,
                expectedSourceIdentity);

        public virtual void DeleteTreeIfExact(
            string siblingName,
            PromotionTreeSnapshot expectedSnapshot) =>
            Inner.DeleteTreeIfExact(
                siblingName,
                expectedSnapshot);

        public void DeleteOwnedTreeIfRootIdentity(
            string siblingName,
            PromotionArtifactIdentity expectedRootIdentity) =>
            Inner.DeleteOwnedTreeIfRootIdentity(
                siblingName,
                expectedRootIdentity);

        public void DeleteFileIfIdentity(
            string siblingName,
            PromotionArtifactIdentity expectedIdentity) =>
            Inner.DeleteFileIfIdentity(
                siblingName,
                expectedIdentity);

        public void Dispose() => Inner.Dispose();
    }

    private sealed class ArtifactConstructionObserver(
        Action<string>? afterCandidateCreateBeforeValidation = null,
        Action<string>? afterJournalCreateBeforeValidation = null,
        Action<string>? candidateCreated = null,
        Action<string>? beforeDiagnosticFlush = null,
        Action<string>? beforeConstructionCleanup = null)
        : IPromotionArtifactConstructionObserver
    {
        public void AfterCandidateCreateBeforeValidation(
            string siblingName) =>
            afterCandidateCreateBeforeValidation?.Invoke(siblingName);

        public void AfterJournalCreateBeforeValidation(
            string siblingName) =>
            afterJournalCreateBeforeValidation?.Invoke(siblingName);

        public void CandidateCreated(string siblingName) =>
            candidateCreated?.Invoke(siblingName);

        public void BeforeDiagnosticFlush(string siblingName) =>
            beforeDiagnosticFlush?.Invoke(siblingName);

        public void BeforeConstructionCleanup(string siblingName) =>
            beforeConstructionCleanup?.Invoke(siblingName);
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) =>
            throw new IOException("injected-writer-failure");

        public override Task WriteLineAsync(string? value) =>
            Task.FromException(
                new IOException("injected-writer-failure"));
    }

    private sealed class RestoreFailingLease(
        IPromotionFileSystemLease inner,
        string destinationName) : DelegatingLease(inner)
    {
        public override void MoveSibling(
            string sourceSiblingName,
            string destinationSiblingName,
            PromotionArtifactIdentity expectedSourceIdentity)
        {
            if (sourceSiblingName.Contains(
                    ".prior-",
                    StringComparison.Ordinal)
                && destinationSiblingName.Equals(
                    destinationName,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "injected-restore-failure");
            }

            base.MoveSibling(
                sourceSiblingName,
                destinationSiblingName,
                expectedSourceIdentity);
        }
    }

    private sealed class PriorCleanupFailingLease(
        IPromotionFileSystemLease inner) : DelegatingLease(inner)
    {
        public override void DeleteTreeIfExact(
            string siblingName,
            PromotionTreeSnapshot expectedSnapshot)
        {
            if (siblingName.Contains(
                    ".prior-",
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "injected-prior-cleanup-failure");
            }

            base.DeleteTreeIfExact(
                siblingName,
                expectedSnapshot);
        }
    }

    private sealed class PostRenameThrowingLease(
        IPromotionFileSystemLease inner,
        string destinationName) : DelegatingLease(inner)
    {
        private bool _thrown;

        public override void MoveSibling(
            string sourceSiblingName,
            string destinationSiblingName,
            PromotionArtifactIdentity expectedSourceIdentity)
        {
            base.MoveSibling(
                sourceSiblingName,
                destinationSiblingName,
                expectedSourceIdentity);
            if (!_thrown
                && sourceSiblingName.Equals(
                    destinationName,
                    StringComparison.Ordinal)
                && destinationSiblingName.Contains(
                    ".prior-",
                    StringComparison.Ordinal))
            {
                _thrown = true;
                throw new IOException(
                    "injected-post-rename-failure");
            }
        }
    }

    private sealed class PostRenameAmbiguousLease(
        IPromotionFileSystemLease inner,
        string destinationName) : DelegatingLease(inner)
    {
        private bool _ambiguous;

        public override void MoveSibling(
            string sourceSiblingName,
            string destinationSiblingName,
            PromotionArtifactIdentity expectedSourceIdentity)
        {
            base.MoveSibling(
                sourceSiblingName,
                destinationSiblingName,
                expectedSourceIdentity);
            if (sourceSiblingName.Equals(
                    destinationName,
                    StringComparison.Ordinal)
                && destinationSiblingName.Contains(
                    ".prior-",
                    StringComparison.Ordinal))
            {
                _ambiguous = true;
                throw new PromotionFileSystemSafetyException(
                    "promotion-parent-identity-changed");
            }
        }

        public override PromotionArtifactIdentity?
            TryGetSiblingIdentity(string siblingName)
        {
            if (_ambiguous)
            {
                throw new PromotionFileSystemSafetyException(
                    "promotion-parent-identity-changed");
            }

            return base.TryGetSiblingIdentity(siblingName);
        }
    }

    private sealed class ParentIdentityChangingLease(
        IPromotionFileSystemLease inner) : DelegatingLease(inner)
    {
        public override PromotionTreeSnapshot? CaptureDestinationTree() =>
            throw new PromotionFileSystemSafetyException(
                "promotion-parent-identity-changed");
    }

    private sealed class ResidueOnlyFileSystem(string residueName)
        : IPromotionFileSystem
    {
        public IPromotionFileSystemLease OpenLease(
            string sourceRoot,
            string destinationRoot) =>
            new ResidueOnlyLease(
                sourceRoot,
                destinationRoot,
                residueName);

        public IReadOnlyList<BaselineFileManifestEntry> EnumerateTree(
            string root) =>
            throw new NotSupportedException();
    }

    private sealed class ResidueOnlyLease(
        string sourceRoot,
        string destinationRoot,
        string residueName) : IPromotionFileSystemLease
    {
        public string SourceRoot { get; } = sourceRoot;

        public string DestinationRoot { get; } = destinationRoot;

        public string DestinationName { get; } =
            Path.GetFileName(destinationRoot);

        public IReadOnlyList<string> ListPromotionResidue() =>
            [residueName];

        public PromotionTreeSnapshot CaptureSourceTree() =>
            throw new NotSupportedException();

        public PromotionTreeSnapshot? CaptureDestinationTree() =>
            throw new NotSupportedException();

        public PromotionTreeSnapshot CaptureSiblingTree(
            string siblingName) =>
            throw new NotSupportedException();

        public PromotionArtifactIdentity? TryGetSiblingIdentity(
            string siblingName) =>
            throw new NotSupportedException();

        public Task<PromotionTreeSnapshot>
            CopySourceToSiblingCreateNewAsync(
                string siblingName,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public PromotionArtifactIdentity CreateDiagnosticFile(
            string siblingName,
            ReadOnlySpan<byte> content) =>
            throw new NotSupportedException();

        public void MoveSibling(
            string sourceSiblingName,
            string destinationSiblingName,
            PromotionArtifactIdentity expectedSourceIdentity) =>
            throw new NotSupportedException();

        public void DeleteTreeIfExact(
            string siblingName,
            PromotionTreeSnapshot expectedSnapshot) =>
            throw new NotSupportedException();

        public void DeleteOwnedTreeIfRootIdentity(
            string siblingName,
            PromotionArtifactIdentity expectedRootIdentity) =>
            throw new NotSupportedException();

        public void DeleteFileIfIdentity(
            string siblingName,
            PromotionArtifactIdentity expectedIdentity) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private sealed class AncestorSwappingTrustPolicy
        : IPromotionFileSystemTrustPolicy
    {
        private readonly string _sourceAncestor;
        private readonly string _parkedAncestor;
        private readonly int _triggerValidationCount;
        private int _validationCount;

        public AncestorSwappingTrustPolicy(
            string sourceAncestor,
            string parkedAncestor)
        {
            _sourceAncestor = sourceAncestor;
            _parkedAncestor = parkedAncestor;
            var root = Path.GetPathRoot(sourceAncestor)
                ?? throw new InvalidDataException(
                    "test-source-root-missing");
            _triggerValidationCount = 1
                + Path.GetRelativePath(root, sourceAncestor)
                    .Split(
                        [
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        ],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Length;
        }

        public bool Swapped { get; private set; }

        public void EnsureTrustedHandle(
            SafeFileHandle handle,
            bool requireCreateTrust,
            bool rejectUnsafeInheritance)
        {
            _validationCount++;
            if (_validationCount == _triggerValidationCount)
            {
                Swap();
            }
        }

        private void Swap()
        {
            if (Swapped)
            {
                return;
            }

            Directory.Move(_sourceAncestor, _parkedAncestor);
            var replacement = Path.Combine(
                _sourceAncestor,
                "source");
            Directory.CreateDirectory(replacement);
            File.WriteAllText(
                Path.Combine(replacement, "artifact.txt"),
                "attacker-source");
            Swapped = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
