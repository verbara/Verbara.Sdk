namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY DIGITS number escapeDigits</summary>
public sealed class SayDigitsCommand : AgiCommandBase
{
    public string? Digits { get; set; }
    public string? EscapeDigits { get; set; }

    public override string BuildCommand() => $"SAY DIGITS {Digits} {EscapeDigits}";
}
