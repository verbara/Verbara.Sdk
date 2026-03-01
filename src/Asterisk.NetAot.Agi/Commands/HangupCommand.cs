namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: HANGUP</summary>
public sealed class HangupCommand : AgiCommandBase
{
    public string? Channel { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "HANGUP";
    }
}
