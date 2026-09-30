namespace TCFUploader.Tests.TestDoubles;

internal sealed class TestPaths : IDisposable
{
    internal TestPaths()
    {
        Root = Path.Combine(FindProjectRoot(), "artifacts", "test-work", Guid.NewGuid().ToString("N"));
        Watch = Path.Combine(Root, "watch");
        Local = Path.Combine(Root, "local");
        Directory.CreateDirectory(Watch);
        Directory.CreateDirectory(Local);
    }
    internal string Root { get; }
    internal string Watch { get; }
    internal string Local { get; }
    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "TCFUploader.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate the TCFUploader project root.");
    }
}
