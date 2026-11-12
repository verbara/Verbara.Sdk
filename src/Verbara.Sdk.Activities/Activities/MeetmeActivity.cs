using Verbara.Sdk;

namespace Verbara.Sdk.Activities.Activities;

/// <summary>
/// Join a conference room with ConfBridge. To join an app_meetme room on an Asterisk that still builds it, call
/// <c>IAgiChannel.ExecAsync("MeetMe", args)</c>.
/// </summary>
public sealed class MeetmeActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string RoomNumber { get; init; }
    public string? Options { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var args = Options is not null ? $"{RoomNumber},{Options}" : RoomNumber;
        await Channel.ExecAsync("ConfBridge", args, cancellationToken);
    }
}
