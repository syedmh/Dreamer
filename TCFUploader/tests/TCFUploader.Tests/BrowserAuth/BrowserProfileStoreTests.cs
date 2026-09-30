using TCFUploader.BrowserAuth;

namespace TCFUploader.Tests.BrowserAuth;

[TestClass]
public sealed class BrowserProfileStoreTests
{
    [TestMethod]
    public async Task DeleteAsync_WaitsForDelayedBrowserFileLockRelease()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), $"tcfuploader-profile-{Guid.NewGuid():N}");
        try
        {
            var store = new BrowserProfileStore(localAppData);
            var created = (BrowserProfileResult.Success)store.Create();
            var lockedFile = Path.Combine(created.Path, "browser.lock");
            await File.WriteAllTextAsync(lockedFile, "locked");

            var fileLock = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var deleteTask = store.DeleteAsync(created.Path, CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await fileLock.DisposeAsync();

            Assert.IsTrue(await deleteTask);
            Assert.IsFalse(Directory.Exists(created.Path));
        }
        finally
        {
            if (Directory.Exists(localAppData))
                Directory.Delete(localAppData, recursive: true);
        }
    }
}
