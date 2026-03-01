using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Bridge two channels together.</summary>
public sealed class BridgeActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string TargetChannel { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        await Channel.ExecAsync("Bridge", TargetChannel, cancellationToken);
    }
}
