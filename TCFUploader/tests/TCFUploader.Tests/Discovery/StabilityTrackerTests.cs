using TCFUploader.Discovery;
using TCFUploader.Files;

namespace TCFUploader.Tests.Discovery;

[TestClass]
public sealed class StabilityTrackerTests
{
    [TestMethod]
    public void AC05_UnstableLockedOrMissingFile_DoesNotSchedulePut()
    {
        var tracker = new StabilityTracker(TimeSpan.FromSeconds(2));
        var path = Path.GetFullPath("sample.jpg");
        tracker.Register(path);
        var start = DateTime.UtcNow;
        var original = new FileObservation(10, start);
        var changed = new FileObservation(11, start.AddSeconds(1));
        Assert.AreEqual(0, tracker.Observe(start, _ => original).Count);
        Assert.AreEqual(0, tracker.Observe(start.AddSeconds(2), _ => original).Count);
        Assert.AreEqual(0, tracker.Observe(start.AddSeconds(4), _ => changed).Count);
        Assert.AreEqual(0, tracker.Observe(start.AddSeconds(6), _ => changed).Count);
        Assert.AreEqual(1, tracker.Observe(start.AddSeconds(8), _ => changed).Count);

        tracker.Register(path, start.AddSeconds(10));
        Assert.AreEqual(0, tracker.Observe(start.AddSeconds(10), _ => null).Count);
        Assert.AreEqual(0, tracker.Observe(start.AddSeconds(12), _ => changed).Count);
        Assert.AreEqual(0, tracker.Observe(start.AddSeconds(14), _ => changed).Count);
        Assert.AreEqual(1, tracker.Observe(start.AddSeconds(16), _ => changed).Count);
    }

    [TestMethod]
    public void CandidateTracking_IsBoundedAndUnavailableEntriesArePrunedForReconciliation()
    {
        var tracker = new StabilityTracker(TimeSpan.FromSeconds(2), capacity: 2);
        var now = DateTime.UtcNow;
        Assert.IsTrue(tracker.Register(Path.GetFullPath("a.jpg"), now));
        Assert.IsTrue(tracker.Register(Path.GetFullPath("b.jpg"), now));
        Assert.IsFalse(tracker.Register(Path.GetFullPath("c.jpg"), now));
        for (var i = 0; i < 3; i++)
            tracker.Observe(now.AddSeconds(i * 2), _ => null);
        Assert.AreEqual(0, tracker.Count);
        Assert.IsTrue(tracker.Register(Path.GetFullPath("c.jpg"), now.AddSeconds(6)));
    }

    [TestMethod]
    public void CandidateTracking_RotatesContinuouslyChangingEntriesAfterBoundedLease()
    {
        var tracker = new StabilityTracker(TimeSpan.FromSeconds(2), capacity: 1);
        var path = Path.GetFullPath("changing.jpg");
        var now = DateTime.UtcNow;
        Assert.IsTrue(tracker.Register(path, now));

        for (var i = 0; i < 6; i++)
        {
            var observed = now.AddSeconds(i * 2);
            Assert.AreEqual(0, tracker.Observe(
                observed,
                _ => new FileObservation(i + 1, observed)).Count);
        }

        Assert.AreEqual(0, tracker.Count);
        Assert.IsTrue(tracker.Register(Path.GetFullPath("later.jpg"), now.AddSeconds(12)));
    }
}
