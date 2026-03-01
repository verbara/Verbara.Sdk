namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: CONTROLSTREAMFILE</summary>
public sealed class ControlStreamFileCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? EscapeDigits { get; set; }
    public int? Offset { get; set; }
    public string? ForwardDigit { get; set; }
    public string? RewindDigit { get; set; }
    public string? PauseDigit { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "CONTROLSTREAMFILE";
    }
}
