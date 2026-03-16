using Asterisk.Sdk;
using Asterisk.Sdk.Activities.Models;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Dial an endpoint and wait for answer or timeout.</summary>
public sealed class DialActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required EndPoint Target { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string? Options { get; init; }

    /// <summary>DIALSTATUS channel variable captured after Dial execution (ANSWER, BUSY, NOANSWER, CANCEL, CONGESTION, CHANUNAVAIL).</summary>
    public string? DialStatus { get; private set; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var dialStr = Target.ToString();
        var timeoutSec = (int)Timeout.TotalSeconds;
        var args = Options is not null
            ? $"{dialStr},{timeoutSec},{Options}"
            : $"{dialStr},{timeoutSec}";

        await Channel.ExecAsync("Dial", args, cancellationToken);
        DialStatus = await Channel.GetVariableAsync("DIALSTATUS", cancellationToken);
    }
}
