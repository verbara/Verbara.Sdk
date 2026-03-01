namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: SPEECH DESTROY</summary>
public sealed class SpeechDestroyCommand : AgiCommandBase
{
    public override string BuildCommand() => "SPEECH DESTROY";
}
