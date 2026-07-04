using System.Text;

namespace Lattice.Tests;

/// <summary>In-memory duplex stream: Read serves scripted chunks in order; Write records into Written.</summary>
internal sealed class ScriptedStream : Stream
{
    private readonly Queue<byte[]> _chunks;
    private byte[]? _current;
    private int _pos;

    public MemoryStream Written { get; } = new();

    public ScriptedStream(params byte[][] chunks) => _chunks = new Queue<byte[]>(chunks);

    /// <summary>One chunk per reply, each with the 0x03 terminator appended.</summary>
    public static ScriptedStream FromReplies(params string[] replies) =>
        new(replies.Select(r => Encoding.UTF8.GetBytes(r + "\x03")).ToArray());

    public override int Read(Span<byte> buffer)
    {
        if (_current is null || _pos >= _current.Length)
        {
            if (!_chunks.TryDequeue(out _current)) return 0;
            _pos = 0;
        }
        int n = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        new(Read(buffer.Span));

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => Written.Write(buffer);
    public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    { Written.Write(buffer.Span); return ValueTask.CompletedTask; }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
