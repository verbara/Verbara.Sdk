namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SPEECH ACTIVATE GRAMMAR</summary>
public sealed class SpeechActivateGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH ACTIVATE GRAMMAR";
    }
}
