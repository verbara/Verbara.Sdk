using Asterisk.Sdk;

namespace Asterisk.Sdk.Activities.Activities;

/// <summary>Send the channel to a queue.</summary>
public sealed class QueueActivity(IAgiChannel channel) : ActivityBase(channel)
{
    public required string QueueName { get; init; }
    public string? Options { get; init; }
    public TimeSpan? Timeout { get; init; }

    protected override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
    {
        var args = QueueName;
        if (Options is not null) args += $",{Options}";
        if (Timeout.HasValue) args += $",,{(int)Timeout.Value.TotalSeconds}";
        await Channel.ExecAsync("Queue", args, cancellationToken);
    }
}
