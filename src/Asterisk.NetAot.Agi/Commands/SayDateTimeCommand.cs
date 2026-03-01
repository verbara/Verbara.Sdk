namespace Asterisk.NetAot.Agi.Commands;

/// <summary>AGI command: SAYDATETIME</summary>
public sealed class SayDateTimeCommand : AgiCommandBase
{
    public long? Time { get; set; }
    public string? EscapeDigits { get; set; }
    public string? Format { get; set; }
    public string? Timezone { get; set; }
    public override string BuildCommand()
    {
        // TODO: Build full command string with parameters
        return "SAYDATETIME";
    }
}
