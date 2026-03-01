namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH ACTIVATE GRAMMAR name</summary>
public sealed class SpeechActivateGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }

    public override string BuildCommand() => $"SPEECH ACTIVATE GRAMMAR {Name}";
}
