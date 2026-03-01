namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH LOAD GRAMMAR</summary>
public sealed class SpeechLoadGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH LOAD GRAMMAR";
    }
}
