namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: GET OPTION</summary>
public sealed class GetOptionCommand : AgiCommandBase
{
    public string? File { get; set; }
    public string? EscapeDigits { get; set; }
    public long? Timeout { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "GET OPTION";
    }
}
