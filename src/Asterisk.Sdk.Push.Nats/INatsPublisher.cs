namespace Asterisk.Sdk.Push.Nats;

/// <summary>
/// Minimal publish-only abstraction over a NATS connection. Introduced so
/// <see cref="NatsBridge"/> can be exercised in unit tests without a real NATS server.
/// The production implementation wraps <c>NATS.Client.Core.NatsConnection</c>.
/// </summary>
public interface INatsPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publish raw UTF-8 JSON bytes to the given NATS subject. Must be thread-safe.
    /// </summary>
    ValueTask PublishAsync(string subject, byte[] payload, CancellationToken cancellationToken = default);
}
