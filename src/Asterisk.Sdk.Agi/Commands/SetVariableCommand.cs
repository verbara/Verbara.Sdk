namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET VARIABLE</summary>
public sealed class SetVariableCommand : AgiCommandBase
{
    public string? Variable { get; set; }
    public string? Value { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET VARIABLE";
    }
}
