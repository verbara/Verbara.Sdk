namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY DIGITS</summary>
public sealed class SayDigitsCommand : AgiCommandBase
{
    public string? Digits { get; set; }
    public string? EscapeDigits { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SAY DIGITS";
    }
}
