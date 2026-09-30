using System.Diagnostics;
using TCFUploader.Configuration;

namespace TCFUploader.BrowserAuth;

internal interface IBrowserProcess : IDisposable
{
    bool HasExited { get; }
    void KillTree();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal interface IBrowserProcessLauncher
{
    IBrowserProcess Launch(BrowserExecutable executable, string profilePath);
}

internal sealed class BrowserProcessLauncher : IBrowserProcessLauncher
{
    public IBrowserProcess Launch(BrowserExecutable executable, string profilePath)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable.Path,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add($"--user-data-dir={profilePath}");
        start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add(executable.Kind == BrowserKind.Edge ? "--inprivate" : "--incognito");
        start.ArgumentList.Add(UploaderConstants.DashboardUploadUri.AbsoluteUri);

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("The browser process did not start.");
        return new BrowserProcess(process);
    }
}

internal sealed class BrowserProcess(Process process) : IBrowserProcess
{
    public bool HasExited => process.HasExited;

    public void KillTree() => process.Kill(entireProcessTree: true);

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public void Dispose() => process.Dispose();
}
