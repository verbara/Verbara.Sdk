namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SEND IMAGE</summary>
public sealed class SendImageCommand : AgiCommandBase
{
    public string? Image { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SEND IMAGE";
    }
}
