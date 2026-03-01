namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY PHONETIC string escapeDigits</summary>
public sealed class SayPhoneticCommand : AgiCommandBase
{
    public string? Text { get; set; }
    public string? EscapeDigits { get; set; }

    public override string BuildCommand() => $"SAY PHONETIC {Text} {EscapeDigits}";
}
