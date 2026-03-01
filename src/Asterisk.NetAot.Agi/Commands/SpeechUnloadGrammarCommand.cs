namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SPEECH UNLOAD GRAMMAR</summary>
public sealed class SpeechUnloadGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH UNLOAD GRAMMAR";
    }
}
