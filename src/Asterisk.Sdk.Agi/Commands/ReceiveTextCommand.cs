namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: RECEIVE TEXT</summary>
public sealed class ReceiveTextCommand : AgiCommandBase
{
    public int? Timeout { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "RECEIVE TEXT";
    }
}
