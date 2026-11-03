namespace Verbara.Sdk.Live.Server;

/// <summary>An immutable snapshot of a Status request refused by Asterisk.</summary>
public sealed class StatusRefusal
{
    internal StatusRefusal(string message, DateTimeOffset occurredAt)
    {
        Message = message;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    /// <summary>The refusal message returned by Asterisk.</summary>
    public string Message { get; }

    /// <summary>When the refusal was read, in UTC.</summary>
    public DateTimeOffset OccurredAt { get; }
}
