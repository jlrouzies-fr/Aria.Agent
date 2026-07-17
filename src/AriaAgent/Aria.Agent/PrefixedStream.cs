namespace Aria.Agent;

/// <summary>
/// Read-only stream that replays already-consumed head bytes (from a peek) before continuing
/// with the inner stream. Used by <see cref="UniversalReasoningHandler"/> to inspect the first
/// SSE chunk without losing it.
/// </summary>
internal sealed class PrefixedStream(byte[] head, int headLength, Stream inner) : Stream
{
    private int _pos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos < headLength)
        {
            var n = Math.Min(count, headLength - _pos);
            Array.Copy(head, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        return inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos < headLength)
        {
            var n = Math.Min(buffer.Length, headLength - _pos);
            head.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        return await inner.ReadAsync(buffer, ct);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
