namespace TCFUploader.Upload;

internal sealed class AttemptAdmission : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource stoppedCts = new();
    private readonly CancellationTokenRegistration stopRegistration;
    private bool stopped;

    internal AttemptAdmission(CancellationToken stopStartingAttempts)
    {
        stopRegistration = stopStartingAttempts.Register(
            static state => ((AttemptAdmission)state!).Stop(),
            this);
    }

    internal bool TryStart<T>(Func<Task<T>> start, out Task<T>? started)
    {
        lock (sync)
        {
            if (stopped)
            {
                started = null;
                return false;
            }

            // Invoking the async operation is the admission linearization point. Cancellation
            // cannot close admission until the operation has synchronously returned its Task.
            started = start();
            return true;
        }
    }

    internal bool IsStopped
    {
        get
        {
            lock (sync)
                return stopped;
        }
    }

    internal CancellationToken StoppedToken => stoppedCts.Token;

    internal void Stop()
    {
        var cancel = false;
        lock (sync)
        {
            if (stopped)
                return;
            stopped = true;
            cancel = true;
        }
        if (cancel)
            stoppedCts.Cancel();
    }

    public void Dispose()
    {
        stopRegistration.Dispose();
        stoppedCts.Dispose();
    }
}
