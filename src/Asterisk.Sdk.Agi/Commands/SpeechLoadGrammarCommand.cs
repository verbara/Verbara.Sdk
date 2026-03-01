namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH LOAD GRAMMAR name path</summary>
public sealed class SpeechLoadGrammarCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public string? Path { get; set; }

    public override string BuildCommand() => $"SPEECH LOAD GRAMMAR {Name} {Path}";
}
