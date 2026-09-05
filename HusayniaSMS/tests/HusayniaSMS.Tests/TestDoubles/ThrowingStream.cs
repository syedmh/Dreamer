namespace HusayniaSMS.Tests.TestDoubles;

internal sealed class ThrowingStream(
    Stream inner,
    bool throwOnWrite = false,
    bool throwOnFlush = false)
    : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush()
    {
        if (throwOnFlush)
        {
            throw new IOException("Injected flush failure.");
        }

        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        throwOnFlush
            ? Task.FromException(new IOException("Injected flush failure."))
            : inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (throwOnWrite)
        {
            throw new IOException("Injected write failure.");
        }

        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (throwOnWrite)
        {
            return ValueTask.FromException(new IOException("Injected write failure."));
        }

        return inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
