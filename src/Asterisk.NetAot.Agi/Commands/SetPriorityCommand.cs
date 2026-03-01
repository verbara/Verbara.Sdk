namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SET PRIORITY</summary>
public sealed class SetPriorityCommand : AgiCommandBase
{
    public string? Priority { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET PRIORITY";
    }
}
