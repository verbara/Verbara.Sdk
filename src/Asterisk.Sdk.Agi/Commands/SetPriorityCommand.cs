namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET PRIORITY priority</summary>
public sealed class SetPriorityCommand : AgiCommandBase
{
    public string? Priority { get; set; }

    public override string BuildCommand() => $"SET PRIORITY {Priority}";
}
