using System.Runtime.CompilerServices;

using NATS.Client.Core;

namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// <see cref="INatsSubscriber"/> implementation backed by a real
/// <see cref="NatsConnection"/>. Does not own the connection lifetime by default — the
/// connection is typically shared with <see cref="NatsConnectionPublisher"/> when the
/// bridge is configured bidirectionally. Pass <c>ownsConnection: true</c> when the
/// subscriber is used in isolation (e.g. receive-only bridges).
/// </summary>
internal sealed class NatsConnectionSubscriber : INatsSubscriber
{
    private readonly NatsConnection _connection;
    private readonly bool _ownsConnection;
    private int _disposed;

    public NatsConnectionSubscriber(NatsConnection connection, bool ownsConnection = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
    }

    public async IAsyncEnumerable<NatsSubscriberMessage> SubscribeAsync(
        string subject,
        string? queueGroup,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        // NATS.Client.Core v2 SubscribeAsync<byte[]>(subject, queueGroup?, deserializer?, opts?, ct)
        // uses the raw deserializer by default — no reflection, AOT-safe.
        await foreach (var msg in _connection
            .SubscribeAsync<byte[]>(subject, queueGroup, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            var payload = msg.Data ?? Array.Empty<byte>();
            yield return new NatsSubscriberMessage(msg.Subject, payload);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_ownsConnection) return;

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown path — nothing upstream can react.
        }
    }
}
