using Verbara.Sdk.Push.Events;

namespace Verbara.Sdk.AotCanary;

internal sealed record CanaryPushEvent : PushEvent
{
    public override string EventType => "canary.ping";
}
