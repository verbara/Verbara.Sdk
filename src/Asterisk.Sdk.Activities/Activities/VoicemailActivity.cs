using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Send the channel to voicemail.</summary>
public sealed class VoicemailActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string Mailbox { get; init; }
    public string? Context { get; init; }
    public bool Urgent { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var target = Context is not null ? $"{Mailbox}@{Context}" : Mailbox;
        var options = Urgent ? "u" : "";
        await Channel.ExecAsync("VoiceMail", $"{target},{options}", cancellationToken);
    }
}
