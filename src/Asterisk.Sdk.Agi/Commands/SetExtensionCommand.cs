namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET EXTENSION</summary>
public sealed class SetExtensionCommand : AgiCommandBase
{
    public string? Extension { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET EXTENSION";
    }
}
