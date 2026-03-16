using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Send the channel to a queue.</summary>
public sealed class QueueActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string QueueName { get; init; }
    public string? Options { get; init; }
    public TimeSpan? Timeout { get; init; }

    /// <summary>QUEUESTATUS channel variable captured after Queue execution (TIMEOUT, FULL, JOINEMPTY, LEAVEEMPTY, JOINUNAVAIL, LEAVEUNAVAIL).</summary>
    public string? QueueStatus { get; private set; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        // Queue(queuename,options,URL,announceoverride,timeout)
        var args = QueueName;
        if (Options is not null || Timeout.HasValue)
        {
            args += $",{Options ?? string.Empty}";
            if (Timeout.HasValue)
            {
                // positions 3 and 4 (URL, announceoverride) are empty
                args += $",,,{(int)Timeout.Value.TotalSeconds}";
            }
        }

        await Channel.ExecAsync("Queue", args, cancellationToken);
        QueueStatus = await Channel.GetVariableAsync("QUEUESTATUS", cancellationToken);
    }
}
