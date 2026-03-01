namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: RECEIVE CHAR</summary>
public sealed class ReceiveCharCommand : AgiCommandBase
{
    public int? Timeout { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "RECEIVE CHAR";
    }
}
