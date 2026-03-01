namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH DEACTIVATE GRAMMAR name</summary>
public sealed class SpeechDeactivateGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }

    public override string BuildCommand() => $"SPEECH DEACTIVATE GRAMMAR {Name}";
}
