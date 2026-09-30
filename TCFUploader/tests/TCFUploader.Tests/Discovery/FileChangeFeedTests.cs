using TCFUploader.Discovery;

namespace TCFUploader.Tests.Discovery;

[TestClass]
public sealed class FileChangeFeedTests
{
    [TestMethod]
    [DataRow("photo.JPG")]
    [DataRow("photo.JpEg")]
    [DataRow("photo.pNg")]
    public void Upsert_SupportedMixedCaseExtension_IsEmitted(string path)
    {
        var change = FileChangeFeed.TranslateUpsert(path);

        Assert.IsNotNull(change);
        Assert.AreEqual(FileChangeKind.Upsert, change.Value.Kind);
        Assert.AreEqual(path, change.Value.FullPath);
    }

    [TestMethod]
    [DataRow("photo.gif")]
    [DataRow("photo.webp")]
    [DataRow("video.mp4")]
    [DataRow("video.mov")]
    [DataRow("notes.txt")]
    [DataRow("extensionless")]
    [DataRow("photo.jpg.exe")]
    public void Upsert_UnsupportedExtension_IsNotEmitted(string path) =>
        Assert.IsNull(FileChangeFeed.TranslateUpsert(path));

    [TestMethod]
    public void Rename_EmitsDeleteAndUpsertAccordingToOldAndNewSupport()
    {
        CollectionAssert.AreEqual(
            new[] { new FileChange(FileChangeKind.Delete, "old.jpg") },
            FileChangeFeed.TranslateRename("old.jpg", "new.gif").ToArray());
        CollectionAssert.AreEqual(
            new[] { new FileChange(FileChangeKind.Upsert, "new.PNG") },
            FileChangeFeed.TranslateRename("old.gif", "new.PNG").ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                new FileChange(FileChangeKind.Delete, "old.JPEG"),
                new FileChange(FileChangeKind.Upsert, "new.png")
            },
            FileChangeFeed.TranslateRename("old.JPEG", "new.png").ToArray());
        Assert.AreEqual(0, FileChangeFeed.TranslateRename("old.gif", "new.webp").Count);
    }
}
