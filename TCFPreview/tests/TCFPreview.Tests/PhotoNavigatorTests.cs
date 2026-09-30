using TCFPreview.Core;

namespace TCFPreview.Tests;

[TestClass]
public sealed class PhotoNavigatorTests
{
    [TestMethod]
    public void EmptyNavigatorHasNoCurrentPhotoOrMovement()
    {
        PhotoNavigator navigator = new([]);

        Assert.IsNull(navigator.CurrentPath);
        Assert.AreEqual(-1, navigator.CurrentIndex);
        Assert.AreEqual(0, navigator.Count);
        Assert.IsFalse(navigator.CanMovePrevious);
        Assert.IsFalse(navigator.CanMoveNext);
        Assert.IsFalse(navigator.TryMovePrevious(out string? previous));
        Assert.IsNull(previous);
        Assert.IsFalse(navigator.TryMoveNext(out string? next));
        Assert.IsNull(next);
    }

    [TestMethod]
    public void NavigationStopsAtBothBoundaries()
    {
        PhotoNavigator navigator = new(["one.jpg", "two.jpg", "three.jpg"]);

        Assert.AreEqual("one.jpg", navigator.CurrentPath);
        Assert.IsFalse(navigator.TryMovePrevious(out string? firstBoundary));
        Assert.AreEqual("one.jpg", firstBoundary);

        Assert.IsTrue(navigator.TryMoveNext(out string? second));
        Assert.AreEqual("two.jpg", second);
        Assert.IsTrue(navigator.TryMoveNext(out string? third));
        Assert.AreEqual("three.jpg", third);
        Assert.IsFalse(navigator.TryMoveNext(out string? lastBoundary));
        Assert.AreEqual("three.jpg", lastBoundary);

        Assert.IsTrue(navigator.TryMovePrevious(out second));
        Assert.AreEqual("two.jpg", second);
        Assert.AreEqual(1, navigator.CurrentIndex);
    }
}
