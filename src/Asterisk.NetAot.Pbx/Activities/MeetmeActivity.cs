using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Pbx.Activities;

/// <summary>Join a conference room (MeetMe or ConfBridge).</summary>
public sealed class MeetmeActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string RoomNumber { get; init; }
    public string? Options { get; init; }
    public bool UseConfBridge { get; init; } = true;

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var app = UseConfBridge ? "ConfBridge" : "MeetMe";
        var args = Options is not null ? $"{RoomNumber},{Options}" : RoomNumber;
        await Channel.ExecAsync(app, args, cancellationToken);
    }
}
