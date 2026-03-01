using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Pbx.Activities;

/// <summary>Park the current call.</summary>
public sealed class ParkActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public string? ParkingLot { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var args = ParkingLot is not null ? $"default,{ParkingLot}" : "";
        await Channel.ExecAsync("Park", args, cancellationToken);
    }
}
