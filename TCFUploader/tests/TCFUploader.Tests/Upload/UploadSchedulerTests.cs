using TCFUploader.Upload;

namespace TCFUploader.Tests.Upload;

[TestClass]
public sealed class UploadSchedulerTests
{
    [TestMethod]
    public void BoundedQueue_RejectsNonblockingWriteWhenFull()
    {
        var scheduler = new UploadScheduler(1);
        Assert.IsTrue(scheduler.TrySchedule(new UploadWork("a")));
        Assert.IsFalse(scheduler.TrySchedule(new UploadWork("b")));
    }
}
