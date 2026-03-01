namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SET CONTEXT</summary>
public sealed class SetContextCommand : AgiCommandBase
{
    public string? Context { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET CONTEXT";
    }
}
