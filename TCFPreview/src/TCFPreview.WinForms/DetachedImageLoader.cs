using System.Drawing;

namespace TCFPreview.WinForms;

public static class DetachedImageLoader
{
    public static Bitmap Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Image source = Image.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
        return new Bitmap(source);
    }
}
