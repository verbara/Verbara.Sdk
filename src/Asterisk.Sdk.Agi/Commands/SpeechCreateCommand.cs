namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH CREATE engine</summary>
public sealed class SpeechCreateCommand : AgiCommandBase
{
    public string? Engine { get; set; }

    public override string BuildCommand() => $"SPEECH CREATE {Engine}";
}
