namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: EXEC Queue — places the current channel into a queue.</summary>
public sealed class QueueCommand : AgiCommandBase
{
    public string? QueueName { get; set; }
    public string? Options { get; set; }

    public override string BuildCommand() =>
        Options is not null ? $"EXEC Queue {QueueName},{Options}" : $"EXEC Queue {QueueName}";
}
