using System.Net.WebSockets;

namespace Verbara.Sdk.VoiceAi.OpenAiRealtime.Tests.Internal;

/// <summary>Intercepts the actual stream write underneath ManagedWebSocket, not its send call.</summary>
internal sealed class HeldWriteTransport : IDisposable
{
    private readonly HttpMessageInvoker _invoker;
    private int _armed;
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool WriteTokenCanBeCancelled { get; private set; }
    internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ClientWebSocket? Socket { get; private set; }
    internal bool ThrowOnHeldWrite { get; init; }
    internal TaskCompletionSource WriteEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void Arm() => Volatile.Write(ref _armed, 1);
    internal HeldWriteTransport()
    {
        _invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            PlaintextStreamFilter = (context, _) => ValueTask.FromResult<Stream>(new HeldStream(context.PlaintextStream, this))
        });
    }
    internal async Task ConnectAsync(ClientWebSocket ws, Uri uri, TimeSpan limit, TimeProvider clock, CancellationToken ct)
    {
        Socket = ws;
        using var bound = new CancellationTokenSource(limit, clock);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);
        try { await ws.ConnectAsync(uri, _invoker, connect.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WebSocketException(WebSocketError.Faulted, "The injected transport's bounded upgrade expired.", new TimeoutException("No upgrade answer within the supplied limit.", ex));
        }
    }
    public void Dispose()
    {
        Release.TrySetResult();
        _invoker.Dispose();
    }
    private sealed class HeldStream(Stream inner, HeldWriteTransport owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref owner._armed, 0) == 1)
            {
                owner.WriteTokenCanBeCancelled = cancellationToken.CanBeCanceled;
                owner.Entered.TrySetResult();
                using var registration = cancellationToken.Register(() => owner.Cancelled.TrySetResult());
                try
                {
                    if (owner.ThrowOnHeldWrite) throw new IOException("Held write seam is live.");
                    // A real pending network write ends when its stream is disposed. Holding it does
                    // not make it immune to transport teardown, and input cancellation does not release it.
                    await Task.WhenAny(owner.Release.Task, owner.Disposed.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(owner.Disposed.Task.IsCompleted, this);
                }
                finally { owner.WriteEnded.TrySetResult(); }
            }
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Disposed.TrySetResult();
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
