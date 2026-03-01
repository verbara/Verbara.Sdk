namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY ALPHA</summary>
public sealed class SayAlphaCommand : AgiCommandBase
{
    public string? Text { get; set; }
    public string? EscapeDigits { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SAY ALPHA";
    }
}
