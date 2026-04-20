namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Minimal subscribe-only abstraction over a NATS connection, parallel to
/// <see cref="INatsPublisher"/>. Introduced so <see cref="NatsBridge"/> can be
/// exercised in unit tests without a real NATS server. The production implementation
/// wraps <c>NATS.Client.Core.NatsConnection</c>.
/// </summary>
public interface INatsSubscriber : IAsyncDisposable
{
    /// <summary>
    /// Subscribe to <paramref name="subject"/> (standard NATS wildcards apply) and yield
    /// every raw message as it arrives. Must be safe to iterate concurrently with
    /// publishes on the same connection.
    /// </summary>
    /// <param name="subject">The NATS subject filter.</param>
    /// <param name="queueGroup">Optional queue group for work-queue semantics.</param>
    /// <param name="cancellationToken">Stops the iteration cleanly.</param>
    IAsyncEnumerable<NatsSubscriberMessage> SubscribeAsync(
        string subject,
        string? queueGroup,
        CancellationToken cancellationToken);
}

/// <summary>
/// Minimal NATS message projection — subject plus raw UTF-8 payload bytes. Decouples
/// the bridge from the concrete <c>NATS.Client.Core.NatsMsg&lt;byte[]&gt;</c> type so
/// fakes can be constructed without referencing the client assembly.
/// </summary>
/// <param name="Subject">The concrete NATS subject the message arrived on.</param>
/// <param name="Payload">The raw wire bytes — never null; empty when the publisher sent no payload.</param>
public readonly record struct NatsSubscriberMessage(string Subject, byte[] Payload);
