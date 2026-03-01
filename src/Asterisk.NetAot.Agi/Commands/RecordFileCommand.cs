namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: RECORD FILE</summary>
public sealed class RecordFileCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? Format { get; set; }
    public string? EscapeDigits { get; set; }
    public int? Timeout { get; set; }
    public int? Offset { get; set; }
    public bool? Beep { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "RECORD FILE";
    }
}
