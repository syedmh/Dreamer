using TCFUploader.Discovery;
using TCFUploader.Tests.TestDoubles;

namespace TCFUploader.Tests.Discovery;

[TestClass]
public sealed class ReconcilerTests
{
    [TestMethod]
    public async Task RecursiveScan_FindsNestedRegularFiles()
    {
        using var paths = new TestPaths();
        Directory.CreateDirectory(Path.Combine(paths.Watch, "nested"));
        var file = Path.Combine(paths.Watch, "nested", "a.jpg");
        await File.WriteAllTextAsync(file, "x");
        var result = await new Reconciler().ScanBatchAsync(paths.Watch, 100, default);
        CollectionAssert.Contains(result.Files.ToList(), Path.GetFullPath(file));
    }

    [TestMethod]
    public async Task IterativeScan_FindsFileAtDeepSupportedDepthWithoutCallStackRecursion()
    {
        using var paths = new TestPaths();
        var directory = paths.Watch;
        for (var depth = 0; depth < 240; depth++)
        {
            directory = Path.Combine(directory, "d");
            Directory.CreateDirectory(directory);
        }
        var file = Path.Combine(directory, "deep.jpg");
        await File.WriteAllBytesAsync(file, [1]);

        var result = await new Reconciler(maxTraversalDepth: 256)
            .ScanBatchAsync(paths.Watch, 10, default);

        CollectionAssert.Contains(result.Files.ToList(), Path.GetFullPath(file));
        Assert.AreEqual(0, result.SkippedChildCount);
    }

    [TestMethod]
    public async Task IterativeScan_DepthLimitSkipsDeeperTreeAndContinuesWithSibling()
    {
        using var paths = new TestPaths();
        var sibling = Path.Combine(paths.Watch, "sibling.jpg");
        await File.WriteAllBytesAsync(sibling, [1]);
        var directory = paths.Watch;
        for (var depth = 0; depth < 5; depth++)
        {
            directory = Path.Combine(directory, $"d{depth}");
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllBytesAsync(Path.Combine(directory, "too-deep.jpg"), [2]);

        var result = await new Reconciler(maxTraversalDepth: 2)
            .ScanBatchAsync(paths.Watch, 10, default);

        CollectionAssert.Contains(result.Files.ToList(), Path.GetFullPath(sibling));
        Assert.AreEqual(1, result.SkippedChildCount);
        CollectionAssert.Contains(
            result.SkippedChildren.ToList(),
            Path.Combine(paths.Watch, "d0", "d1", "d2"));
    }

    [TestMethod]
    public async Task Reconciliation_BatchesNamespaceWithoutPermanentlyMissingFiles()
    {
        using var paths = new TestPaths();
        for (var i = 0; i < 11; i++)
            await File.WriteAllTextAsync(Path.Combine(paths.Watch, $"file-{i}.jpg"), i.ToString());
        var reconciler = new Reconciler();
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReconcileResult batch;
        do
        {
            batch = await reconciler.ScanBatchAsync(paths.Watch, 3, default);
            foreach (var file in batch.Files)
                discovered.Add(file);
            Assert.IsTrue(batch.Files.Count <= 3);
        } while (batch.HasMore);
        Assert.AreEqual(11, discovered.Count);
    }

    [TestMethod]
    public async Task Reconciliation_StreamContinuation_ReachesStableTailDespiteFullBatchAheadOfTargetChurn()
    {
        using var paths = new TestPaths();
        const int batchSize = 4;
        for (var i = 0; i < 24; i++)
            await File.WriteAllTextAsync(Path.Combine(paths.Watch, $"m-{i:D3}.jpg"), i.ToString());
        var stableTail = Path.GetFullPath(Path.Combine(paths.Watch, "z-tail.jpg"));
        await File.WriteAllTextAsync(stableTail, "tail");

        var reconciler = new Reconciler();
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maxBatchCount = 0;
        for (var pass = 0; pass < 12 && !discovered.Contains(stableTail); pass++)
        {
            var batch = await reconciler.ScanBatchAsync(paths.Watch, batchSize, default);
            maxBatchCount = Math.Max(maxBatchCount, batch.Files.Count);
            foreach (var file in batch.Files)
                discovered.Add(file);

            var progressName = batch.Files
                .Select(Path.GetFileName)
                .Where(name => string.Compare(name, "z-tail.jpg", StringComparison.OrdinalIgnoreCase) < 0)
                .DefaultIfEmpty($"m-{pass:D3}.jpg")
                .Max(StringComparer.OrdinalIgnoreCase)!;
            for (var churn = 0; churn < batchSize; churn++)
            {
                var insertedName = $"{progressName}~{pass:D3}-{churn:D2}.jpg";
                Assert.IsTrue(
                    string.Compare(insertedName, progressName, StringComparison.OrdinalIgnoreCase) > 0 &&
                    string.Compare(insertedName, "z-tail.jpg", StringComparison.OrdinalIgnoreCase) < 0,
                    $"Churn path {insertedName} was not strictly between {progressName} and z-tail.jpg.");
                await File.WriteAllTextAsync(Path.Combine(paths.Watch, insertedName), "new");
            }
        }

        Assert.IsTrue(discovered.Contains(stableTail), "Stable tail file was starved by path churn.");
        Assert.IsTrue(maxBatchCount <= batchSize);
    }

    [TestMethod]
    public async Task Reconciliation_SilentlySkipsUnsupportedFilesWhileTraversingDirectories()
    {
        using var paths = new TestPaths();
        var nested = Path.Combine(paths.Watch, "nested");
        Directory.CreateDirectory(nested);
        var supported = new[]
        {
            Path.Combine(paths.Watch, "one.JpG"),
            Path.Combine(nested, "two.JPEG"),
            Path.Combine(nested, "three.pNg")
        };
        var unsupported = new[]
        {
            Path.Combine(paths.Watch, "image.gif"),
            Path.Combine(paths.Watch, "image.webp"),
            Path.Combine(paths.Watch, "video.mp4"),
            Path.Combine(paths.Watch, "video.mov"),
            Path.Combine(paths.Watch, "notes.txt"),
            Path.Combine(paths.Watch, "extensionless"),
            Path.Combine(paths.Watch, "photo.jpg.exe")
        };
        foreach (var file in supported.Concat(unsupported))
            await File.WriteAllBytesAsync(file, [1]);

        var result = await new Reconciler().ScanBatchAsync(paths.Watch, 100, default);

        CollectionAssert.AreEquivalent(
            supported.Select(Path.GetFullPath).ToArray(),
            result.Files.ToArray());
        Assert.AreEqual(0, result.SkippedChildCount);
        Assert.AreEqual(0, result.SkippedChildren.Count);
    }

    [TestMethod]
    public async Task Reconciliation_ReportsSkippedReparsePointChildren()
    {
        using var paths = new TestPaths();
        var outside = Path.Combine(paths.Root, "outside");
        var link = Path.Combine(paths.Watch, "linked-child");
        Directory.CreateDirectory(outside);
        CreateJunction(link, outside);
        try
        {
            var result = await new Reconciler().ScanBatchAsync(paths.Watch, 10, default);
            Assert.AreEqual(1, result.SkippedChildCount);
            CollectionAssert.Contains(result.SkippedChildren.ToList(), link);
        }
        finally
        {
            RemoveJunction(link);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode,
            $"mklink failed: {process.StandardOutput.ReadToEnd()} {process.StandardError.ReadToEnd()}");
    }

    private static void RemoveJunction(string link)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/d /c rmdir \"{link}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
    }
}
