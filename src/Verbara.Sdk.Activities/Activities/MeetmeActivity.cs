using Verbara.Sdk;

namespace Verbara.Sdk.Activities.Activities;

/// <summary>Join a conference room (MeetMe or ConfBridge).</summary>
public sealed class MeetmeActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string RoomNumber { get; init; }
    public string? Options { get; init; }
    [Obsolete(
        "app_meetme is deprecated since Asterisk 19 and is not built by default; false selects MeetMe. No replacement: ConfBridge is the default, so remove the assignment. This member is removed in Verbara.Sdk 2.10.0.",
        DiagnosticId = "VSDK0005",
        UrlFormat = "https://github.com/verbara/Verbara.Sdk/blob/main/docs/guides/obsolete-ami-events-removal-migration.md#vsdk0005")]
    public bool UseConfBridge { get; init; } = true;

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
#pragma warning disable VSDK0005 // reads the deprecated MeetMe switch until its removal; ConfBridge stays the default
        var app = UseConfBridge ? "ConfBridge" : "MeetMe";
#pragma warning restore VSDK0005
        var args = Options is not null ? $"{RoomNumber},{Options}" : RoomNumber;
        await Channel.ExecAsync(app, args, cancellationToken);
    }
}
