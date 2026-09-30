using System.Drawing;
using System.Drawing.Imaging;
using TCFPreview.WinForms;

namespace TCFPreview.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DetachedImageLoaderTests
{
    public static IEnumerable<object[]> SupportedImageFormats
    {
        get
        {
            yield return ["jpg", ImageFormat.Jpeg];
            yield return ["jpeg", ImageFormat.Jpeg];
            yield return ["png", ImageFormat.Png];
            yield return ["bmp", ImageFormat.Bmp];
            yield return ["gif", ImageFormat.Gif];
            yield return ["tif", ImageFormat.Tiff];
            yield return ["tiff", ImageFormat.Tiff];
        }
    }

    [TestMethod]
    [DynamicData(nameof(SupportedImageFormats))]
    public void Load_SupportsEveryAdvertisedImageFormat(
        string extension,
        ImageFormat imageFormat)
    {
        using TempDirectory directory = new();
        string imagePath = Path.Combine(directory.Path, $"preview.{extension}");
        using (Bitmap original = new(4, 3))
        {
            original.Save(imagePath, imageFormat);
        }

        using Bitmap loaded = DetachedImageLoader.Load(imagePath);
        File.Delete(imagePath);

        Assert.IsFalse(File.Exists(imagePath));
        Assert.AreEqual(4, loaded.Width);
        Assert.AreEqual(3, loaded.Height);
    }

    [TestMethod]
    public void Load_ReturnsUsableBitmapWithoutLockingSource()
    {
        using TempDirectory directory = new();
        string imagePath = Path.Combine(directory.Path, "preview.bmp");
        using (Bitmap original = new(4, 3))
        {
            original.SetPixel(0, 0, Color.CornflowerBlue);
            original.Save(imagePath, ImageFormat.Bmp);
        }

        using Bitmap loaded = DetachedImageLoader.Load(imagePath);
        File.Delete(imagePath);

        Assert.IsFalse(File.Exists(imagePath));
        Assert.AreEqual(4, loaded.Width);
        Assert.AreEqual(3, loaded.Height);
        Assert.AreEqual(Color.CornflowerBlue.ToArgb(), loaded.GetPixel(0, 0).ToArgb());
    }
}
