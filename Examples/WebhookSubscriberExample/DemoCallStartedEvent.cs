using Asterisk.Sdk.Push.Events;

namespace WebhookSubscriberExample;

public sealed record DemoCallStartedEvent : PushEvent
{
    public override string EventType => "calls.inbound.started";
}
