namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SEND TEXT</summary>
public sealed class SendTextCommand : AgiCommandBase
{
    public string? Text { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SEND TEXT";
    }
}
