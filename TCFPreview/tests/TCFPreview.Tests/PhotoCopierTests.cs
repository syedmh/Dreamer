using TCFPreview.Core;

namespace TCFPreview.Tests;

[TestClass]
public sealed class PhotoCopierTests
{
    [TestMethod]
    public async Task CopyAsync_WithoutCollisionPreservesExactNameBytesAndSource()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        byte[] contents = [0, 1, 2, 127, 128, 254, 255];
        string sourcePath = source.CreateFile("Original Name.JPEG", contents);

        CopyResult result = await PhotoCopier.CopyAsync(sourcePath, destination.Path);

        Assert.AreEqual(sourcePath, result.SourcePath);
        Assert.AreEqual("Original Name.JPEG", Path.GetFileName(result.DestinationPath));
        Assert.IsTrue(File.Exists(sourcePath));
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(sourcePath));
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(result.DestinationPath));
        Assert.IsEmpty(Directory.GetFiles(destination.Path, ".tcfpreview-*.tmp"));
    }

    [TestMethod]
    public async Task CopyAsync_PreservesNameAndCreatesNumberedCollisionNames()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        byte[] contents = [10, 20, 30, 40];
        string sourcePath = source.CreateFile("photo.jpg", contents);
        destination.CreateFile("photo.jpg", [1]);
        destination.CreateFile("photo (1).jpg", [2]);

        CopyResult result = await PhotoCopier.CopyAsync(sourcePath, destination.Path);

        Assert.AreEqual("photo (2).jpg", Path.GetFileName(result.DestinationPath));
        CollectionAssert.AreEqual(contents, File.ReadAllBytes(result.DestinationPath));
        CollectionAssert.AreEqual(
            new byte[] { 1 },
            File.ReadAllBytes(Path.Combine(destination.Path, "photo.jpg")));
        CollectionAssert.AreEqual(
            new byte[] { 2 },
            File.ReadAllBytes(Path.Combine(destination.Path, "photo (1).jpg")));
        Assert.IsEmpty(Directory.GetFiles(destination.Path, ".tcfpreview-*.tmp"));
    }

    [TestMethod]
    public async Task CopyAsync_ConcurrentCopiesUseUniqueNamesWithoutOverwriting()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        byte[] contents = [10, 20, 30, 40];
        string sourcePath = source.CreateFile("photo.jpg", contents);
        destination.CreateFile("photo.jpg", [99]);

        CopyResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => PhotoCopier.CopyAsync(sourcePath, destination.Path)));

        string[] destinationNames = results
            .Select(result => Path.GetFileName(result.DestinationPath))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.HasCount(8, destinationNames);
        Assert.HasCount(8, destinationNames.Distinct(StringComparer.Ordinal));
        CollectionAssert.AreEqual(
            Enumerable.Range(1, 8)
                .Select(index => $"photo ({index}).jpg")
                .Order(StringComparer.Ordinal)
                .ToArray(),
            destinationNames);
        CollectionAssert.AreEqual(
            new byte[] { 99 },
            File.ReadAllBytes(Path.Combine(destination.Path, "photo.jpg")));

        foreach (CopyResult result in results)
        {
            CollectionAssert.AreEqual(contents, File.ReadAllBytes(result.DestinationPath));
        }

        Assert.IsEmpty(Directory.GetFiles(destination.Path, ".tcfpreview-*.tmp"));
    }

    [TestMethod]
    public async Task CopyAsync_CancellationRemovesPartialDestinationAndStagingFiles()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg", [1, 2, 3, 4]);
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => PhotoCopier.CopyAsync(
                sourcePath,
                destination.Path,
                async (_, output, token) =>
                {
                    await output.WriteAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                },
                cancellation.Token));

        Assert.IsEmpty(Directory.GetFiles(destination.Path));
    }

    [TestMethod]
    public async Task CopyAsync_CopyFailureRemovesPartialDestinationAndStagingFiles()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg", [1, 2, 3, 4]);

        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => PhotoCopier.CopyAsync(
                sourcePath,
                destination.Path,
                async (_, output, _) =>
                {
                    await output.WriteAsync(new byte[] { 10, 20, 30 });
                    throw new IOException("simulated copy failure");
                }));

        Assert.AreEqual("simulated copy failure", exception.Message);
        Assert.IsEmpty(Directory.GetFiles(destination.Path));
    }

    [TestMethod]
    public async Task CopyAsync_CopyAndCleanupFailuresPreserveCopyFailure()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg", [1, 2, 3, 4]);
        IOException copyFailure = new("simulated copy failure");

        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(
            () => PhotoCopier.CopyAsync(
                sourcePath,
                destination.Path,
                (_, _, _) => Task.FromException(copyFailure),
                _ => throw new UnauthorizedAccessException("simulated cleanup failure")));

        Assert.AreSame(copyFailure, exception);
        Assert.IsInstanceOfType<UnauthorizedAccessException>(
            exception.Data["PhotoCopier.CleanupException"]);
    }

    [TestMethod]
    public async Task CopyAsync_CancellationAndCleanupFailuresPreserveCancellation()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg", [1, 2, 3, 4]);
        OperationCanceledException cancellationFailure = new("simulated cancellation");

        OperationCanceledException exception =
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => PhotoCopier.CopyAsync(
                    sourcePath,
                    destination.Path,
                    (_, _, _) => Task.FromException(cancellationFailure),
                    _ => throw new UnauthorizedAccessException("simulated cleanup failure")));

        Assert.AreSame(cancellationFailure, exception);
        Assert.IsInstanceOfType<UnauthorizedAccessException>(
            exception.Data["PhotoCopier.CleanupException"]);
    }

    [TestMethod]
    public async Task CopyAsync_SuccessfulPublicationDoesNotAttemptStagingCleanup()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg", [1, 2, 3, 4]);

        CopyResult result = await PhotoCopier.CopyAsync(
            sourcePath,
            destination.Path,
            static (input, output, token) => input.CopyToAsync(output, token),
            _ => throw new AssertFailedException("Published staging path must not be deleted."));

        Assert.AreEqual(
            Path.Combine(destination.Path, "photo.jpg"),
            result.DestinationPath);
        Assert.IsTrue(File.Exists(result.DestinationPath));
    }

    [TestMethod]
    public async Task CopyAsync_ReusedStagingPathAfterPublicationIsNotDeleted()
    {
        using TempDirectory source = new();
        using TempDirectory destination = new();
        string sourcePath = source.CreateFile("photo.jpg", [1, 2, 3, 4]);
        byte[] reusedContents = [9, 8, 7, 6];
        string? stagingPath = null;

        CopyResult result = await PhotoCopier.CopyAsync(
            sourcePath,
            destination.Path,
            async (input, output, token) =>
            {
                stagingPath = ((FileStream)output).Name;
                await input.CopyToAsync(output, token);
            },
            File.Delete,
            (sourceStagingPath, destinationPath) =>
            {
                File.Move(sourceStagingPath, destinationPath, overwrite: false);
                File.WriteAllBytes(sourceStagingPath, reusedContents);
            });

        Assert.IsNotNull(stagingPath);
        Assert.IsTrue(File.Exists(result.DestinationPath));
        Assert.IsTrue(File.Exists(stagingPath));
        CollectionAssert.AreEqual(reusedContents, File.ReadAllBytes(stagingPath));
    }

    [TestMethod]
    public async Task CopyAsync_MissingSourceThrows()
    {
        using TempDirectory destination = new();
        string missingSource = Path.Combine(destination.Path, "missing.jpg");

        FileNotFoundException exception = await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => PhotoCopier.CopyAsync(missingSource, destination.Path));

        Assert.AreEqual(missingSource, exception.FileName);
    }

    [TestMethod]
    public async Task CopyAsync_MissingDestinationThrows()
    {
        using TempDirectory source = new();
        string sourcePath = source.CreateFile("photo.jpg");
        string missingDestination = Path.Combine(source.Path, "missing");

        DirectoryNotFoundException exception =
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
                () => PhotoCopier.CopyAsync(sourcePath, missingDestination));

        StringAssert.Contains(exception.Message, missingDestination);
    }
}
