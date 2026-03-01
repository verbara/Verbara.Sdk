namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: GET VARIABLE</summary>
public sealed class GetVariableCommand : AgiCommandBase
{
    public string? Variable { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "GET VARIABLE";
    }
}
