using Asterisk.Sdk.Push.Events;

namespace NatsBridgeExample;

public sealed record DemoBridgeEvent : PushEvent
{
    public override string EventType => "demo.bridge";
    public string? Note { get; init; } = "sample payload";
}
