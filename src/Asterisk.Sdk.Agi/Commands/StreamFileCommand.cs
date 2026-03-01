namespace Asterisk.Sdk.Agi.Commands;

/// <summary>AGI command: STREAM FILE</summary>
public sealed class StreamFileCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? EscapeDigits { get; set; }
    public int? Offset { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "STREAM FILE";
    }
}
