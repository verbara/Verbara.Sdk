namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH DEACTIVATE GRAMMAR</summary>
public sealed class SpeechDeactivateGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH DEACTIVATE GRAMMAR";
    }
}
