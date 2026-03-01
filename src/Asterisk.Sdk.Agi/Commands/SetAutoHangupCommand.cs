namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SET AUTOHANGUP</summary>
public sealed class SetAutoHangupCommand : AgiCommandBase
{
    public int? Time { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET AUTOHANGUP";
    }
}
