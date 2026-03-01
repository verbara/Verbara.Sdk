using Asterisk.NetAot.Abstractions;

namespace Asterisk.NetAot.Pbx.Activities;

/// <summary>Hang up the current channel.</summary>
public sealed class HangupActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public int? CauseCode { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        await Channel.HangupAsync(cancellationToken);
    }
}
