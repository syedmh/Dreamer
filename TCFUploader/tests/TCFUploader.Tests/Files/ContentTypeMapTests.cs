using TCFUploader.Files;

namespace TCFUploader.Tests.Files;

[TestClass]
public sealed class ContentTypeMapTests
{
    [TestMethod]
    public void AcceptedExtensions_ExposeOnlyRequiredMimeTypes()
    {
        Assert.AreEqual("image/jpeg", ContentTypeMap.Get(".JPG"));
        Assert.AreEqual("image/jpeg", ContentTypeMap.Get(".jPeG"));
        Assert.AreEqual("image/png", ContentTypeMap.Get(".PnG"));
        Assert.AreEqual("application/octet-stream", ContentTypeMap.Get(".gif"));
        Assert.AreEqual("application/octet-stream", ContentTypeMap.Get(".mp4"));
        Assert.AreEqual("application/octet-stream", ContentTypeMap.Get(".unknown"));
    }

    [TestMethod]
    public void LegacyStoredContentTypes_RemainReadable()
    {
        Assert.IsTrue(ContentTypeMap.IsCompatibleStoredContentType(".gif", "image/gif"));
        Assert.IsTrue(ContentTypeMap.IsCompatibleStoredContentType(".mp4", "video/mp4"));
        Assert.IsTrue(ContentTypeMap.IsCompatibleStoredContentType(".bin", "application/octet-stream"));
        Assert.IsFalse(ContentTypeMap.IsCompatibleStoredContentType(".gif", "application/octet-stream"));
    }
}
