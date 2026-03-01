namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SPEECH SET</summary>
public sealed class SpeechSetCommand : AgiCommandBase
{
    public string? Name { get; set; }
    public string? Value { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SPEECH SET";
    }
}
