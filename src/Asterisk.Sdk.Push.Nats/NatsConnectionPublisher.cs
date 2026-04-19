using NATS.Client.Core;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// <see cref="INatsPublisher"/> implementation backed by a real
/// <see cref="NatsConnection"/>. Owns the connection lifetime — call <c>DisposeAsync</c>
/// to drain and disconnect gracefully.
/// </summary>
internal sealed class NatsConnectionPublisher : INatsPublisher
{
    private readonly NatsConnection _connection;
    private int _disposed;

    public NatsConnectionPublisher(NatsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    public ValueTask PublishAsync(string subject, byte[] payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // NATS.Client.Core v2 PublishAsync<T>(subject, data, ...) with byte[] uses the default
        // raw serializer — no reflection, AOT-safe. Bytes are written verbatim to the wire.
        return _connection.PublishAsync<byte[]>(subject, payload, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallow dispose errors — we are shutting down and nothing upstream can react.
        }
    }
}
