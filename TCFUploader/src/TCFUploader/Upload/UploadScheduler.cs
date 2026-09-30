using System.Threading.Channels;

namespace TCFUploader.Upload;

internal sealed class UploadScheduler
{
    private readonly Channel<UploadWork> channel;
    private readonly Action<int>? observeCount;
    internal UploadScheduler(int capacity, Action<int>? observeCount = null)
    {
        this.observeCount = observeCount;
        channel = Channel.CreateBounded<UploadWork>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }
    internal ChannelReader<UploadWork> Reader => channel.Reader;
    internal async ValueTask ScheduleAsync(UploadWork work, CancellationToken cancellationToken)
    {
        await channel.Writer.WriteAsync(work, cancellationToken);
        observeCount?.Invoke(channel.Reader.Count);
    }
    internal bool TrySchedule(UploadWork work)
    {
        var scheduled = channel.Writer.TryWrite(work);
        if (scheduled) observeCount?.Invoke(channel.Reader.Count);
        return scheduled;
    }
    internal void Complete() => channel.Writer.TryComplete();
}
