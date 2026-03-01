namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH CREATE</summary>
public sealed class SpeechCreateCommand : AgiCommandBase
{
    public string? Engine { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH CREATE";
    }
}
