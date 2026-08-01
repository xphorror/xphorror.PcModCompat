namespace JALib.Tools;

public class ProgressStream(Stream baseStream, long length) : Stream
{
    private long _position;
    private long _lastCheckedPosition;

    public bool NeedUpdate(out double value)
    {
        if (Length == -1)
        {
            value = 0;
            return false;
        }
        var currentPosition = Interlocked.Read(ref _position);
        if (currentPosition == Interlocked.Read(ref _lastCheckedPosition))
        {
            value = 0;
            return false;
        }
        value = Length == 0 ? 1d : (double)currentPosition / Length;
        Interlocked.Exchange(ref _lastCheckedPosition, currentPosition);
        return true;
    }

    public override void Flush() => baseStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken)
        => baseStream.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = baseStream.Read(buffer, offset, count);
        Interlocked.Add(ref _position, read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = baseStream.Read(buffer);
        Interlocked.Add(ref _position, read);
        return read;
    }

    public override int ReadByte()
    {
        var read = baseStream.ReadByte();
        if (read != -1)
            Interlocked.Increment(ref _position);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await baseStream.ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Add(ref _position, read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await baseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _position, read);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length { get; } = length;

    public override long Position
    {
        get => Interlocked.Read(ref _position);
        set => throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            baseStream.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => baseStream.DisposeAsync();
}
