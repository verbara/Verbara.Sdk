namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GETFULLVARIABLE</summary>
public sealed class GetFullVariableCommand : AgiCommandBase
{
    public string? Variable { get; set; }
    public string? Channel { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "GETFULLVARIABLE";
    }
}
