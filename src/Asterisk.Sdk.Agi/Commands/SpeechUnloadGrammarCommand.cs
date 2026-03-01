namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH UNLOAD GRAMMAR name</summary>
public sealed class SpeechUnloadGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }

    public override string BuildCommand() => $"SPEECH UNLOAD GRAMMAR {Name}";
}
