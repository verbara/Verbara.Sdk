namespace Verbara.Sdk.Ari.Tests.TestSupport;

/// <summary>
/// A read side that plays a script: each read returns the next scripted chunk, and every read after
/// the script is spent throws the given exception, or returns 0 (end of stream) when there is none.
/// This is how a session is handed a transport failure without a socket: an <see cref="IOException"/>
/// here is what a reset connection surfaces from <c>NetworkStream.ReadAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// An empty script makes the very first read the failure (or the end of stream), which is a
/// connection that fails before it has sent anything. Empty chunks are skipped: a read that returns 0
/// is the end of stream, and a script says that with no exception, not with an empty chunk.
/// </para>
/// <para>
/// When <c>release</c> is given, the first read past the script waits for it before it throws or
/// returns 0, so a test decides when the failure lands relative to what it has already observed:
/// ordered by construction, not by a delay. <see cref="ScriptSpent"/> completes as that read begins.
/// A release that never completes parks the read until the reader's token is cancelled, which is an
/// idle connection that only its owner ends.
/// </para>
/// <para>
/// Writes are accepted and discarded. Each chunk must fit the buffer the reader offers; a chunk that
/// does not is a mistake in the test and fails the read.
/// </para>
/// </remarks>
internal sealed class ScriptedStream : Stream
{
    private readonly byte[][] _script;
    private readonly Exception? _then;
    private readonly Task _release;
    private readonly TaskCompletionSource _scriptSpent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;
    private int _disposed;

    /// <summary>One scripted read, then <paramref name="then"/> or end of stream.</summary>
    public ScriptedStream(byte[] first, Exception? then)
        : this([first], then)
    {
    }

    public ScriptedStream(IReadOnlyList<byte[]> script, Exception? then, Task? release = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = [.. script.Where(chunk => chunk.Length > 0)];
        _then = then;
        _release = release ?? Task.CompletedTask;
    }

    /// <summary>Completes when the first read past the script begins, before it waits, throws or ends.</summary>
    public Task ScriptSpent => _scriptSpent.Task;

    /// <summary>How many reads have begun.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Whether the reader has disposed this stream.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Always completes asynchronously, as a socket read that has to wait for bytes does.
        await Task.Yield();

        var read = Interlocked.Increment(ref _reads);
        if (read <= _script.Length)
        {
            var chunk = _script[read - 1];
            if (chunk.Length > buffer.Length)
            {
                throw new InvalidOperationException(
                    $"Scripted chunk {read} is {chunk.Length} bytes and the reader offered {buffer.Length}; split it.");
            }

            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        _scriptSpent.TrySetResult();
        await _release.WaitAsync(cancellationToken);

        if (_then is not null)
            throw _then;

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        // Discarded: what a session writes back is not what these tests read.
    }

    public override void Flush()
    {
        // Nothing is buffered.
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Volatile.Write(ref _disposed, 1);
        base.Dispose(disposing);
    }
}
