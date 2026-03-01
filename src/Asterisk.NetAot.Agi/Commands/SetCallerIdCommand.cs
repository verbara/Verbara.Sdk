namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SET CALLERID</summary>
public sealed class SetCallerIdCommand : AgiCommandBase
{
    public string? CallerId { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SET CALLERID";
    }
}
