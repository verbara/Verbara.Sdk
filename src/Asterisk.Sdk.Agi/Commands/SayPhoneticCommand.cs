namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SAY PHONETIC</summary>
public sealed class SayPhoneticCommand : AgiCommandBase
{
    public string? Text { get; set; }
    public string? EscapeDigits { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SAY PHONETIC";
    }
}
