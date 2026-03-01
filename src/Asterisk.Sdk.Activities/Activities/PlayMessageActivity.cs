using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Play an audio file to the channel.</summary>
public sealed class PlayMessageActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string FileName { get; init; }
    public string EscapeDigits { get; init; } = string.Empty;

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        await Channel.StreamFileAsync(FileName, EscapeDigits, cancellationToken);
    }
}
