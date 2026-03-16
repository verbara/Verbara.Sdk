using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Park the current call.</summary>
public sealed class ParkActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public string? ParkingLot { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var args = ParkingLot ?? string.Empty;
        await Channel.ExecAsync("Park", args, cancellationToken);
    }
}
