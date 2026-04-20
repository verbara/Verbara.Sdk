namespace Asterisk.Sdk.Cluster.Primitives;

/// <summary>
/// Base record for all cluster events distributed via an <see cref="IClusterTransport"/>.
/// Consumers typically derive domain-specific event records from this base and publish
/// them through the transport's <see cref="IClusterTransport.PublishAsync"/> method.
/// </summary>
/// <param name="SourceInstanceId">Identifier of the cluster instance that originated the event.</param>
/// <param name="Timestamp">UTC time at which the event was produced.</param>
public abstract record ClusterEvent(string SourceInstanceId, DateTimeOffset Timestamp)
{
    /// <summary>
    /// Optional W3C <c>traceparent</c> (<c>00-{trace-id}-{span-id}-{flags}</c>) for cross-node
    /// distributed tracing. Injected by the publisher when a <see cref="System.Diagnostics.Activity"/>
    /// is active; extracted by the subscriber to continue the trace. Null means no trace context.
    /// </summary>
    public string? TraceContext { get; init; }
}
