using Asterisk.Sdk.Push.Events;

namespace Asterisk.Sdk.AotCanary;

internal sealed record CanaryPushEvent : PushEvent
{
    public override string EventType => "canary.ping";
}
