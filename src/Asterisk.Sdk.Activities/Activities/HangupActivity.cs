using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Hang up the current channel.</summary>
public sealed class HangupActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public int? CauseCode { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        if (CauseCode.HasValue)
            await Channel.ExecAsync("Hangup", CauseCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        else
            await Channel.HangupAsync(cancellationToken);
    }
}
