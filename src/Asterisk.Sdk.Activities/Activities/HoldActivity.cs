using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Put a channel on hold with optional music on hold class.</summary>
public sealed class HoldActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public string? MusicOnHoldClass { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var args = MusicOnHoldClass ?? "default";
        await Channel.ExecAsync("MusicOnHold", args, cancellationToken);
    }
}
