namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY ALPHA number escapeDigits</summary>
public sealed class SayAlphaCommand : AgiCommandBase
{
    public string? Text { get; set; }
    public string? EscapeDigits { get; set; }

    public override string BuildCommand() => $"SAY ALPHA {Text} {EscapeDigits}";
}
