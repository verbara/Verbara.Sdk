namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SPEECH RECOGNIZE</summary>
public sealed class SpeechRecognizeCommand : AgiCommandBase
{
    public string? Prompt { get; set; }
    public int? Timeout { get; set; }
    public int? Offset { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH RECOGNIZE";
    }
}
